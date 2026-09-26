using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KdrEnet.Relay;

/// <summary>
/// Joins two outbound connections that present the same session code.
/// Neither laptop accepts inbound internet traffic. The server only splices
/// the pair, then gets out of the way with Nagle disabled.
/// </summary>
public sealed class RelayHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Waiter>> _waiting = new();

    public async Task RunAsync(IPAddress address, int port, CancellationToken ct)
    {
        var listener = new TcpListener(address, port);
        listener.Server.NoDelay = true;
        listener.Server.ReceiveBufferSize = 512 * 1024;
        listener.Server.SendBufferSize = 512 * 1024;
        listener.Start(512);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                client.NoDelay = true;
                client.ReceiveBufferSize = 512 * 1024;
                client.SendBufferSize = 512 * 1024;
                _ = Task.Run(() => HandleAsync(client, ct));
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string line;
            try
            {
                using var lineBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lineBudget.CancelAfter(TimeSpan.FromSeconds(10));
                line = await ReadLineAsync(stream, 80, lineBudget.Token);
            }
            catch
            {
                return;
            }

            if (!TryParse(line, out var role, out var code, out var port, out var kind))
                return;

            if (role == 'P')
            {
                try { await stream.WriteAsync(OkLine, ct); } catch { /* probe gave up */ }
                return;
            }

            Link link;
            try
            {
                link = await MatchAsync(role, code + ":" + port + ":" + kind, stream, ct);
            }
            catch
            {
                return;
            }

            if (!link.Pump)
            {
                try { await link.Done.Task.WaitAsync(ct); }
                catch { /* the pumper finished or the server is stopping */ }
                return;
            }

            try
            {
                await stream.WriteAsync(OkLine, ct);
                await link.Peer.WriteAsync(OkLine, ct);
                await PumpAsync(stream, link.Peer, ct);
            }
            catch
            {
                // One side hung up. Closing both ends unblocks the other.
            }
            finally
            {
                link.Done.TrySetResult();
                try { stream.Close(); } catch { /* already closed */ }
                try { link.Peer.Close(); } catch { /* already closed */ }
            }
        }
    }

    private async Task<Link> MatchAsync(char role, string key, NetworkStream mine, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Waiter? opposite = null;
            var self = new Waiter { Role = role, Stream = mine };
            lock (_gate)
            {
                if (!_waiting.TryGetValue(key, out var list))
                {
                    list = new List<Waiter>();
                    _waiting[key] = list;
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (IsAlive(list[i].Stream))
                        continue;
                    list[i].Ready.TrySetCanceled();
                    list.RemoveAt(i);
                }

                var index = list.FindIndex(item => item.Role != role && IsAlive(item.Stream));
                if (index >= 0)
                {
                    opposite = list[index];
                    list.RemoveAt(index);
                    if (list.Count == 0)
                        _waiting.Remove(key);
                }
                else
                {
                    list.Add(self);
                }
            }

            if (opposite is not null)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!opposite.Ready.TrySetResult(new Link { Peer = mine, Pump = false, Done = done }))
                    continue;
                return new Link { Peer = opposite.Stream, Pump = true, Done = done };
            }

            using (ct.Register(() => self.Ready.TrySetCanceled(ct)))
            {
                try
                {
                    return await self.Ready.Task.WaitAsync(ct);
                }
                catch
                {
                    lock (_gate)
                    {
                        if (_waiting.TryGetValue(key, out var list))
                        {
                            list.Remove(self);
                            if (list.Count == 0)
                                _waiting.Remove(key);
                        }
                    }

                    throw;
                }
            }
        }
    }

    internal static bool TryParse(string line, out char role, out string code, out int port, out char kind)
    {
        role = '?';
        code = "";
        port = 0;
        kind = '?';
        var parts = line.Split(' ');
        if (parts.Length != 5 || parts[0] != "K1" || parts[1].Length != 1 || parts[4].Length != 1)
            return false;

        role = parts[1][0];
        kind = parts[4][0];
        if (role is not ('C' or 'T' or 'P') || kind is not ('T' or 'U'))
            return false;
        if (parts[2].Length != 6 || !parts[2].All(char.IsDigit))
            return false;
        if (!int.TryParse(parts[3], out port))
            return false;

        code = parts[2];
        if (role == 'P')
            return port == 0;
        return port is > 0 and <= 65535;
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

    private static bool IsAlive(NetworkStream stream)
    {
        try
        {
            var socket = stream.Socket;
            if (!socket.Connected)
                return false;
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private static async Task PumpAsync(NetworkStream left, NetworkStream right, CancellationToken ct)
    {
        var forward = CopyAsync(left, right, ct);
        var back = CopyAsync(right, left, ct);
        try { await Task.WhenAll(forward, back); }
        catch { /* one direction already ended */ }
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break;
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            // The other direction finishes when this side closes its write half.
        }
        finally
        {
            try { to.Socket.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static readonly byte[] OkLine = "OK\n"u8.ToArray();

    private sealed class Waiter
    {
        public char Role;
        public NetworkStream Stream = null!;
        public TaskCompletionSource<Link> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Link
    {
        public NetworkStream Peer = null!;
        public bool Pump;
        public TaskCompletionSource Done { get; set; } = null!;
    }
}
