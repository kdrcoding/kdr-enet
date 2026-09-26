using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace KdrEnet.Services;

/// <summary>
/// Passes diagnostic traffic straight to the car.
/// NoDelay is on so small E-Sys packets are not held for a fraction of a second
/// the way Windows portproxy holds them.
/// </summary>
public sealed class FastRelay : IDisposable
{
    public static readonly int[] TcpPorts = { 6801, 13400, 50160 };
    public static readonly int[] UdpPorts = { 6811, 13400 };

    private readonly List<Socket> _listeners = new();
    private readonly List<Socket> _live = new();
    private readonly List<Task> _loops = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private int _disposed;
    private readonly object _peerGate = new();
    private int _peers;
    public Action<string>? OnClient { get; set; }
    public Action? OnQuiet { get; set; }

    public void Start(IPAddress vehicle, IReadOnlyList<int>? tcpPorts = null, IReadOnlyList<int>? udpPorts = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        foreach (var port in tcpPorts ?? TcpPorts)
            ListenTcp(vehicle, port, port);
        foreach (var port in udpPorts ?? UdpPorts)
            ListenUdp(vehicle, port);
    }

    public void Start(IPAddress vehicle, int listenPort, int targetPort)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ListenTcp(vehicle, listenPort, targetPort);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cancel.Cancel();
        foreach (var socket in _listeners)
        {
            try { socket.Close(); } catch { /* unblocks accept */ }
        }

        Socket[] live;
        lock (_gate)
            live = _live.ToArray();
        foreach (var socket in live)
        {
            try { socket.Close(); } catch { /* unblocks the copy */ }
        }

        try { Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(2)); }
        catch { /* listeners are already closed */ }

        _cancel.Dispose();
    }

    private void ListenTcp(IPAddress vehicle, int listenPort, int targetPort)
    {
        var listen = NewTcpSocket();
        try
        {
            listen.Bind(new IPEndPoint(IPAddress.Any, listenPort));
            listen.Listen(128);
        }
        catch (SocketException ex)
        {
            listen.Dispose();
            throw new InvalidOperationException("Port " + listenPort + " is already in use. Close the other ENET tool and try again.", ex);
        }

        _listeners.Add(listen);
        var token = _cancel.Token;
        _loops.Add(Task.Run(() => AcceptLoop(listen, vehicle, listenPort, targetPort, token)));
    }

    private void ListenUdp(IPAddress vehicle, int port)
    {
        var listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            listen.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            listen.Dispose();
            throw new InvalidOperationException("UDP port " + port + " is already in use.", ex);
        }

        _listeners.Add(listen);
        var token = _cancel.Token;
        _loops.Add(Task.Run(() => UdpLoop(listen, vehicle, port, token)));
    }

    private async Task AcceptLoop(Socket listen, IPAddress vehicle, int listenPort, int targetPort, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket incoming;
            try
            {
                incoming = await listen.AcceptAsync(token);
            }
            catch
            {
                break;
            }

            Tune(incoming);
            _ = Task.Run(() => PumpAsync(incoming, vehicle, targetPort, listenPort));
        }
    }

    private async Task PumpAsync(Socket incoming, IPAddress vehicle, int targetPort, int listenPort)
    {
        using var remote = incoming;
        using var car = NewTcpSocket();
        Track(remote);
        Track(car);
        var reported = false;
        try
        {
            using var connectCancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await car.ConnectAsync(new IPEndPoint(vehicle, targetPort), connectCancel.Token);
            var pump = Task.WhenAll(PipeAsync(remote, car), PipeAsync(car, remote));
            if (listenPort == 6801)
            {
                var done = await Task.WhenAny(pump, Task.Delay(400, _cancel.Token));
                if (done != pump && !_cancel.IsCancellationRequested)
                    reported = ReportArrival(listenPort, remote);
            }

            await pump;
        }
        catch
        {
            // The car side or the other laptop closed. Drop this one connection only.
        }
        finally
        {
            if (reported)
                ReportLeft();
            Forget(remote);
            Forget(car);
        }
    }

    private bool ReportArrival(int listenPort, Socket remote)
    {
        if (listenPort != 6801 || remote.RemoteEndPoint is not IPEndPoint end || end.Address.ToString() is not { Length: > 0 } ip || ip == "127.0.0.1")
            return false;
        lock (_peerGate)
        {
            _peers++;
            try { OnClient?.Invoke(ip); }
            catch { /* the bridge keeps running */ }
        }

        return true;
    }

    private void ReportLeft()
    {
        lock (_peerGate)
        {
            _peers--;
            if (_peers < 0)
                _peers = 0;
            if (_peers > 0)
                return;
            try { OnQuiet?.Invoke(); }
            catch { /* the bridge keeps running */ }
        }
    }

    private void Track(Socket socket)
    {
        lock (_gate)
            _live.Add(socket);
    }

    private void Forget(Socket socket)
    {
        lock (_gate)
            _live.Remove(socket);
    }

    private static async Task PipeAsync(Socket from, Socket to)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None);
                if (read == 0)
                    break;

                var sent = 0;
                while (sent < read)
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None);
            }
        }
        catch
        {
            // The paired direction closes the sockets.
        }
        finally
        {
            try { to.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task UdpLoop(Socket listen, IPAddress vehicle, int port, CancellationToken token)
    {
        var car = new IPEndPoint(vehicle, port);
        EndPoint? lastRemote = null;
        var buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await listen.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            }
            catch
            {
                break;
            }

            if (result.ReceivedBytes <= 0 || result.RemoteEndPoint is not IPEndPoint from)
                continue;

            try
            {
                if (from.Address.Equals(vehicle))
                {
                    if (lastRemote is not null)
                        await listen.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, lastRemote);
                    continue;
                }

                lastRemote = from;
                await listen.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, car);
            }
            catch
            {
                // One lost datagram must not stop the session.
            }
        }
    }

    private static Socket NewTcpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Tune(socket);
        return socket;
    }

    private static void Tune(Socket socket)
    {
        socket.NoDelay = true;
        socket.SendBufferSize = 512 * 1024;
        socket.ReceiveBufferSize = 512 * 1024;
    }
}
