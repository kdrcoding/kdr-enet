using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace KdrEnet.Services;

/// <summary>
/// Carries diagnostic ports across the internet without a VPN.
/// The car laptop and the E-Sys laptop both dial outward. The session server
/// splices the matching code. E-Sys always talks to 127.0.0.1 on its own laptop.
/// </summary>
public sealed class SessionLink : IDisposable
{
    private readonly List<Socket> _listeners = new();
    private readonly List<TcpClient> _clients = new();
    private readonly List<Task> _loops = new();
    private readonly object _clientGate = new();
    private readonly CancellationTokenSource _cancel = new();
    private int _disposed;

    public static string NewCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Digits(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var chars = new char[raw.Length];
        var count = 0;
        foreach (var character in raw)
        {
            if (character is >= '0' and <= '9')
                chars[count++] = character;
        }

        return new string(chars, 0, count);
    }

    public static string FormatCode(string digits)
    {
        digits = Digits(digits);
        return digits.Length == 6 ? digits[..3] + " " + digits[3..] : digits;
    }

    public void StartCar(string code, IPAddress vehicle, string host, int relayPort, IReadOnlyList<int>? tcpPorts = null, IReadOnlyList<int>? udpPorts = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var digits = RequireCode(code);
        Probe(host, relayPort);
        var token = _cancel.Token;
        foreach (var port in tcpPorts ?? FastRelay.TcpPorts)
            _loops.Add(Task.Run(() => CarTcpAsync(digits, vehicle, port, host, relayPort, token)));
        foreach (var port in udpPorts ?? FastRelay.UdpPorts)
            _loops.Add(Task.Run(() => CarUdpAsync(digits, vehicle, port, host, relayPort, token)));
    }

    public void StartTechnician(string code, string host, int relayPort, IReadOnlyList<int>? tcpPorts = null, IReadOnlyList<int>? udpPorts = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var digits = RequireCode(code);
        Probe(host, relayPort);
        var token = _cancel.Token;
        try
        {
            foreach (var port in tcpPorts ?? FastRelay.TcpPorts)
            {
                var listen = NewTcpSocket();
                listen.Bind(new IPEndPoint(IPAddress.Loopback, port));
                listen.Listen(128);
                _listeners.Add(listen);
                var captured = port;
                _loops.Add(Task.Run(() => TechAcceptAsync(listen, digits, captured, host, relayPort, token)));
            }

            foreach (var port in udpPorts ?? FastRelay.UdpPorts)
            {
                var listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                listen.SendBufferSize = 65536;
                listen.ReceiveBufferSize = 65536;
                listen.Bind(new IPEndPoint(IPAddress.Loopback, port));
                _listeners.Add(listen);
                var captured = port;
                _loops.Add(Task.Run(() => TechUdpAsync(listen, digits, captured, host, relayPort, token)));
            }
        }
        catch (SocketException ex)
        {
            Dispose();
            throw new InvalidOperationException("A diagnostic port is already in use on this laptop. Close the other ENET tool and try again.", ex);
        }
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

        lock (_clientGate)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { /* unblocks the handshake */ }
            }
        }

        try { Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(2)); }
        catch { /* listeners are already closed */ }

        _cancel.Dispose();
    }

    private async Task CarTcpAsync(string code, IPAddress vehicle, int port, string host, int relayPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? relay = null;
            try
            {
                relay = await DialAsync(host, relayPort, ct);
                var stream = relay.GetStream();
                await WriteHelloAsync(stream, 'C', code, port, 'T', ct);
                await ReadOkAsync(stream, ct, Timeout.InfiniteTimeSpan);
                var car = NewTcpSocket();
                var held = relay;
                relay = null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        connectBudget.CancelAfter(TimeSpan.FromSeconds(8));
                        await car.ConnectAsync(new IPEndPoint(vehicle, port), connectBudget.Token);
                        await PumpSocketsAsync(held.Client, car, ct);
                    }
                    catch
                    {
                        // This one E-Sys connection ended. The loop is already waiting for the next.
                    }
                    finally
                    {
                        try { car.Close(); } catch { /* already closed */ }
                        Close(held);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Close(relay);
                break;
            }
            catch
            {
                Close(relay);
                await IdleAsync(ct);
            }
        }
    }

    private async Task TechAcceptAsync(Socket listen, string code, int port, string host, int relayPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket incoming;
            try
            {
                incoming = await listen.AcceptAsync(ct);
            }
            catch
            {
                break;
            }

            Tune(incoming);
            _ = Task.Run(() => TechPumpAsync(incoming, code, port, host, relayPort, ct));
        }
    }

    private async Task TechPumpAsync(Socket incoming, string code, int port, string host, int relayPort, CancellationToken ct)
    {
        using var local = incoming;
        TcpClient? relay = null;
        try
        {
            relay = await DialAsync(host, relayPort, ct);
            var stream = relay.GetStream();
            await WriteHelloAsync(stream, 'T', code, port, 'T', ct);
            await ReadOkAsync(stream, ct, TimeSpan.FromSeconds(20));
            await PumpSocketsAsync(local, relay.Client, ct);
        }
        catch
        {
            // E-Sys or the car side closed this one connection.
        }
        finally
        {
            Close(relay);
        }
    }

    private async Task CarUdpAsync(string code, IPAddress vehicle, int port, string host, int relayPort, CancellationToken ct)
    {
        var car = new IPEndPoint(vehicle, port);
        while (!ct.IsCancellationRequested)
        {
            TcpClient? relay = null;
            Socket? udp = null;
            try
            {
                relay = await DialAsync(host, relayPort, ct);
                var stream = relay.GetStream();
                await WriteHelloAsync(stream, 'C', code, port, 'U', ct);
                await ReadOkAsync(stream, ct, Timeout.InfiniteTimeSpan);
                udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SendBufferSize = 65536;
                udp.ReceiveBufferSize = 65536;
                var down = FramesToUdpAsync(stream, udp, car, ct);
                var up = UdpToFramesAsync(udp, stream, null, ct);
                await Task.WhenAny(down, up);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await IdleAsync(ct);
            }
            finally
            {
                Close(relay);
                try { udp?.Close(); } catch { /* already closed */ }
            }
        }
    }

    private async Task TechUdpAsync(Socket listen, string code, int port, string host, int relayPort, CancellationToken ct)
    {
        EndPoint lastRemote = new IPEndPoint(IPAddress.Loopback, 0);
        var haveRemote = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpClient? relay = null;
            try
            {
                relay = await DialAsync(host, relayPort, ct);
                var stream = relay.GetStream();
                await WriteHelloAsync(stream, 'T', code, port, 'U', ct);
                await ReadOkAsync(stream, ct, Timeout.InfiniteTimeSpan);
                var down = FramesToUdpAsync(stream, listen, null, ct, () => Volatile.Read(ref haveRemote) == 1 ? lastRemote : null);
                var up = UdpToFramesAsync(listen, stream, from =>
                {
                    lastRemote = from;
                    Volatile.Write(ref haveRemote, 1);
                }, ct);
                await Task.WhenAny(down, up);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await IdleAsync(ct);
            }
            finally
            {
                Close(relay);
            }
        }
    }

    private async Task<TcpClient> DialAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            Tune(client.Client);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, budget.Token);
            Track(client);
            return client;
        }
        catch
        {
            client.Close();
            throw;
        }
    }

    private static async Task WriteHelloAsync(NetworkStream stream, char role, string code, int port, char kind, CancellationToken ct)
    {
        var line = Encoding.ASCII.GetBytes("K1 " + role + " " + code + " " + port + " " + kind + "\n");
        await stream.WriteAsync(line, ct);
    }

    private static async Task ReadOkAsync(NetworkStream stream, CancellationToken ct, TimeSpan budget)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(budget);
        var line = await ReadLineAsync(stream, 16, limit.Token);
        if (line != "OK")
            throw new IOException("The session server rejected the code.");
    }

    public static int? MeasureMilliseconds(string host, int port)
    {
        if (TimeOnce(host, port) is null)
            return null;

        var samples = new List<int>(3);
        for (var i = 0; i < 3; i++)
        {
            if (TimeOnce(host, port) is int milliseconds)
                samples.Add(milliseconds);
        }

        if (samples.Count == 0)
            return null;

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static int? TimeOnce(string host, int port)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Probe(host, port);
        }
        catch
        {
            return null;
        }

        return (int)clock.ElapsedMilliseconds;
    }

    private static void Probe(string host, int port)
    {
        using var client = new TcpClient();
        var connect = client.ConnectAsync(host, port);
        if (!connect.Wait(TimeSpan.FromSeconds(4)))
            throw new InvalidOperationException("The session server did not answer. The code cannot reach the other laptop until that server is running.");
        connect.GetAwaiter().GetResult();
        client.NoDelay = true;
        using var stream = client.GetStream();
        var hello = Encoding.ASCII.GetBytes("K1 P 000000 0 T\n");
        stream.Write(hello);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var line = ReadLineAsync(stream, 16, budget.Token).GetAwaiter().GetResult();
        if (line != "OK")
            throw new InvalidOperationException("The session server did not accept the connection.");
    }

    private static async Task PumpSocketsAsync(Socket left, Socket right, CancellationToken ct)
    {
        var forward = PipeAsync(left, right, ct);
        var back = PipeAsync(right, left, ct);
        try { await Task.WhenAll(forward, back); }
        catch { /* one direction already ended */ }
    }

    private static async Task PipeAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None, ct);
                if (read == 0)
                    break;
                var sent = 0;
                while (sent < read)
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, ct);
            }
        }
        catch
        {
            // The other direction finishes when this side closes its write half.
        }
        finally
        {
            try { to.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task FramesToUdpAsync(NetworkStream stream, Socket udp, IPEndPoint? fixedTarget, CancellationToken ct, Func<EndPoint?>? target = null)
    {
        var header = new byte[2];
        var payload = new byte[2048];
        while (true)
        {
            await ReadExactAsync(stream, header, 2, ct);
            var length = (header[0] << 8) | header[1];
            if (length <= 0 || length > payload.Length)
                throw new IOException("Bad datagram.");
            await ReadExactAsync(stream, payload, length, ct);
            var destination = fixedTarget ?? target?.Invoke();
            if (destination is null)
                continue;
            await udp.SendToAsync(payload.AsMemory(0, length), SocketFlags.None, destination, ct);
        }
    }

    private static async Task UdpToFramesAsync(Socket udp, NetworkStream stream, Action<EndPoint>? remember, CancellationToken ct)
    {
        var datagram = new byte[2048];
        var frame = new byte[2050];
        while (true)
        {
            var result = await udp.ReceiveFromAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            if (result.ReceivedBytes <= 0)
                continue;
            remember?.Invoke(result.RemoteEndPoint);
            frame[0] = (byte)(result.ReceivedBytes >> 8);
            frame[1] = (byte)result.ReceivedBytes;
            Buffer.BlockCopy(datagram, 0, frame, 2, result.ReceivedBytes);
            await stream.WriteAsync(frame.AsMemory(0, result.ReceivedBytes + 2), ct);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken ct)
    {
        var got = 0;
        while (got < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(got, length - got), ct);
            if (read == 0)
                throw new IOException("closed");
            got += read;
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, int max, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (text.Length < max)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0)
                throw new IOException("closed");
            if (one[0] == (byte)'\n')
                return text.ToString().Trim('\r');
            text.Append((char)one[0]);
        }

        throw new IOException("line too long");
    }

    private void Track(TcpClient client)
    {
        lock (_clientGate)
            _clients.Add(client);
    }

    private void Close(TcpClient? client)
    {
        if (client is null)
            return;
        try { client.Close(); } catch { /* already closed */ }
        lock (_clientGate)
            _clients.Remove(client);
    }

    private static async Task IdleAsync(CancellationToken ct)
    {
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private static string RequireCode(string code)
    {
        var digits = Digits(code);
        if (digits.Length != 6)
            throw new InvalidOperationException("The session code is 6 digits.");
        return digits;
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
