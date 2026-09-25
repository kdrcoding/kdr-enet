using System.Diagnostics;
using System.Net;
using KdrEnet;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KdrEnet.Services;

/// <summary>
/// Opens a temporary ENET path from this laptop to the car.
/// Forwarding is limited to link-local car addresses (169.254.x.x) and the
/// diagnostic ports a remote E-Sys session needs. Stopping removes it.
/// </summary>
public sealed class SessionService
{
    public const string TcpRuleName = "KDR Coding ENET TCP";
    public const string UdpRuleName = "KDR Coding ENET UDP";

    public static readonly int[] TcpPorts = FastRelay.TcpPorts;

    private FastRelay? _relay;
    private SessionLink? _link;

    private static readonly string[] VpnHints =
    {
        "radmin", "vpn", "wireguard", "tailscale", "zerotier", "hamachi",
        "openvpn", "nordlynx", "wintun", "tun", "tap"
    };

    private static readonly string[] SkipHints =
    {
        "loopback", "bluetooth", "vethernet", "hyper-v", "wsl", "virtualbox",
        "vmware", "default switch", "isatap", "teredo", "radmin", "vpn",
        "wireguard", "tailscale", "zerotier", "hamachi"
    };

    public Task<ScanResult> ScanAsync(bool probeVehicle, CancellationToken ct = default)
        => Task.Run(() => ScanCore(probeVehicle), ct);

    private ScanResult ScanCore(bool probeVehicle)
    {
        var technicians = ListTechnicianAddresses();
        var best = technicians.FirstOrDefault();
        var others = technicians.Skip(1).ToArray();
        var title = AppSettings.VehicleLabel;
        var word = AppSettings.VehicleWord;

        if (AppSettings.Cable == CableKind.Kdcan)
            return ScanKdcan(title, word, best, others);

        var adapters = FindDiagAdapters(AppSettings.Cable);
        if (adapters.Count == 0)
        {
            return new ScanResult
            {
                CableState = "wait",
                CableDetail = "Not connected",
                PowerState = "wait",
                PowerDetail = "Waiting",
                VehicleState = "wait",
                VehicleDetail = "Waiting",
                VehicleTitle = title,
                BestTechnician = best,
                OtherTechnicians = others,
                Notes = new[] { "No " + AppSettings.CableLabel + " adapter yet." }
            };
        }

        EnetAdapter? enet = null;
        string? arpIp = null;
        foreach (var adapter in adapters)
        {
            var neighbor = ReadArpNeighbor(adapter.Index, adapter.LocalIp);
            if (neighbor is null)
                continue;
            enet = adapter;
            arpIp = neighbor;
            break;
        }

        enet ??= adapters[0];

        string? vehicleIp = null;
        var source = "";
        if (probeVehicle && arpIp is null && IsLinkLocal(enet.LocalIp))
        {
            var udpIp = ListenForVehicle(enet.LocalIp, TimeSpan.FromMilliseconds(400));
            if (udpIp is not null)
            {
                vehicleIp = udpIp;
                source = "The " + word + " answered on the cable.";
            }
        }

        if (vehicleIp is null && arpIp is not null)
        {
            vehicleIp = arpIp;
            source = "The " + word + " is visible on the cable.";
        }

        var awake = vehicleIp is not null;
        return new ScanResult
        {
            CableState = "ok",
            CableDetail = enet.Name + " · " + enet.LocalIp,
            PowerState = awake ? "ok" : "warn",
            PowerDetail = awake ? "Awake" : "Module quiet",
            VehicleState = awake ? "ok" : "warn",
            VehicleDetail = awake ? vehicleIp! : "Ignition off",
            VehicleIp = vehicleIp,
            VehicleNote = source,
            VehicleTitle = title,
            AdapterName = enet.Name,
            LocalEnetIp = enet.LocalIp,
            BestTechnician = best,
            OtherTechnicians = others,
            Notes = awake
                ? new[] { title + " found at " + vehicleIp + ". The module is awake." }
                : new[] { AppSettings.CableLabel + " is connected. The module is quiet." }
        };
    }

    private static ScanResult ScanKdcan(string title, string word, TechnicianAddress? best, IReadOnlyList<TechnicianAddress> others)
    {
        var port = UsbCableProbe.FindKdcanPort();
        if (port is null)
        {
            return new ScanResult
            {
                CableState = "wait",
                CableDetail = "Not connected",
                PowerState = "wait",
                PowerDetail = "Waiting",
                VehicleState = "wait",
                VehicleDetail = "Waiting",
                VehicleTitle = title,
                BestTechnician = best,
                OtherTechnicians = others,
                Notes = new[] { "No K+DCAN USB cable yet." }
            };
        }

        return new ScanResult
        {
            CableState = "ok",
            CableDetail = port,
            PowerState = "warn",
            PowerDetail = "USB only",
            VehicleState = "wait",
            VehicleDetail = "Not on USB",
            VehicleTitle = title,
            BestTechnician = best,
            OtherTechnicians = others,
            Notes = new[] { "K+DCAN is plugged in. Module power is not visible through this USB cable." }
        };
    }

    public async Task<bool> HasLeftoverSessionAsync(CancellationToken ct = default)
    {
        if (FirewallControl.HasSavedState() || FirewallControl.AllowRuleExists())
            return true;
        if (await FirewallRuleExistsAsync(TcpRuleName, ct))
            return true;

        var rows = await ReadProxiesAsync(ct);
        return rows.Any(row => TcpPorts.Contains(row.ListenPort) && IsLinkLocal(row.ConnectAddress));
    }

    public async Task StartAsync(string vehicleIp, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!IsLinkLocal(vehicleIp))
            throw new InvalidOperationException("The car address is not on the ENET cable.");

        if (IsLocalAddress(vehicleIp))
            throw new InvalidOperationException("That address belongs to this laptop, not the car.");

        var existing = await ReadProxiesAsync(ct);
        foreach (var port in TcpPorts)
        {
            var row = existing.FirstOrDefault(r => r.ListenPort == port && r.ListenAddress is "0.0.0.0");
            if (row is not null && !IsLinkLocal(row.ConnectAddress))
            {
                throw new InvalidOperationException(
                    "Port " + port + " is already forwarded somewhere else. Close the other tool, then try again.");
            }
        }

        Report(progress, "Preparing Windows for the session.");
        await DeleteLegacyRulesAsync(ct);
        await DeleteOurProxiesAsync(ct);

        var firewallMessage = await Task.Run(FirewallControl.Open, ct);
        Report(progress, firewallMessage);

        FastRelay? relay = null;
        try
        {
            relay = new FastRelay();
            relay.Start(IPAddress.Parse(vehicleIp));
            _relay = relay;
            relay = null;
        }
        catch
        {
            relay?.Dispose();
            StopRelay();
            try { await Task.Run(FirewallControl.CloseSession, ct); } catch { /* original error is shown */ }
            throw;
        }

        Report(progress, "Direct link is open to " + vehicleIp + " on ports " + string.Join(", ", TcpPorts) + ". Packets go straight through, with no Windows port-proxy delay.");
    }

    public Task StartLinkAsync(string code, string? vehicleIp, bool carSide, IProgress<string>? progress, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!RelaySettings.TryGet(out var host, out var port))
            {
                throw new InvalidOperationException(
                    "The session server is not set. Add one line, host:7601, to " + RelaySettings.SettingsPath);
            }

            var digits = SessionLink.Digits(code);
            if (digits.Length != 6)
                throw new InvalidOperationException("The session code is 6 digits.");

            IPAddress? vehicle = null;
            if (carSide)
            {
                if (string.IsNullOrWhiteSpace(vehicleIp) || !IsLinkLocal(vehicleIp))
                    throw new InvalidOperationException("The car address is not on the ENET cable.");
                if (IsLocalAddress(vehicleIp))
                    throw new InvalidOperationException("That address belongs to this laptop, not the car.");
                vehicle = IPAddress.Parse(vehicleIp);
            }

            StopLink();
            var link = new SessionLink();
            try
            {
                if (carSide)
                {
                    link.StartCar(digits, vehicle!, host, port);
                    Report(progress, "Session code " + SessionLink.FormatCode(digits) + " is live. Read it to the other person.");
                }
                else
                {
                    link.StartTechnician(digits, host, port);
                    Report(progress, "Joined " + SessionLink.FormatCode(digits) + ". In E-Sys use 127.0.0.1.");
                }

                _link = link;
                link = null;
            }
            catch
            {
                link?.Dispose();
                throw;
            }
        }, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        try { StopRelay(); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { StopLink(); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { await DeleteOurProxiesAsync(ct); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { await DeleteLegacyRulesAsync(ct); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { await Task.Run(FirewallControl.CloseSession, ct); }
        catch (Exception ex) { errors.Add(ex.Message); }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    private void StopRelay()
    {
        var relay = Interlocked.Exchange(ref _relay, null);
        relay?.Dispose();
    }

    private void StopLink()
    {
        var link = Interlocked.Exchange(ref _link, null);
        link?.Dispose();
    }

    private async Task DeleteLegacyRulesAsync(CancellationToken ct)
    {
        await NetshAsync(ct, "advfirewall", "firewall", "delete", "rule", "name=" + TcpRuleName);
        await NetshAsync(ct, "advfirewall", "firewall", "delete", "rule", "name=" + UdpRuleName);
    }

    private async Task DeleteOurProxiesAsync(CancellationToken ct)
    {
        var rows = await ReadProxiesAsync(ct);
        foreach (var row in rows)
        {
            if (!TcpPorts.Contains(row.ListenPort) || !IsLinkLocal(row.ConnectAddress))
                continue;

            await NetshAsync(ct,
                "interface", "portproxy", "delete", "v4tov4",
                "listenport=" + row.ListenPort,
                "listenaddress=" + row.ListenAddress);
        }
    }

    private async Task<bool> FirewallRuleExistsAsync(string name, CancellationToken ct)
    {
        var result = await NetshAsync(ct, "advfirewall", "firewall", "show", "rule", "name=" + name);
        if (result.Code != 0)
            return false;
        return result.Text.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<ProxyRow>> ReadProxiesAsync(CancellationToken ct)
    {
        var result = await NetshAsync(ct, "interface", "portproxy", "show", "v4tov4");
        var rows = new List<ProxyRow>();
        foreach (Match match in Regex.Matches(
                     result.Text,
                     @"(\d+\.\d+\.\d+\.\d+)\s+(\d+)\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+)"))
        {
            rows.Add(new ProxyRow(
                match.Groups[1].Value,
                int.Parse(match.Groups[2].Value),
                match.Groups[3].Value,
                int.Parse(match.Groups[4].Value)));
        }

        return rows;
    }

    private static async Task<CommandResult> NetshAsync(CancellationToken ct, params string[] args)
        => await RunAsync("netsh", args, ct);

    private static async Task<CommandResult> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            return new CommandResult(-1, "", "Could not start " + file);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private static string? ListenForVehicle(string localIp, TimeSpan timeout)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            socket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 6811));
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;

            var probe = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x11 };
            try
            {
                socket.SendTo(probe, new IPEndPoint(IPAddress.Parse("169.254.255.255"), 6811));
            }
            catch (SocketException)
            {
                // Listening still works if the probe cannot leave this adapter.
            }

            var buffer = new byte[2048];
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    break;
                socket.ReceiveTimeout = remaining;
                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    var read = socket.ReceiveFrom(buffer, ref remote);
                    if (read > 0 &&
                        remote is IPEndPoint endPoint &&
                        IsLinkLocal(endPoint.Address.ToString()) &&
                        endPoint.Address.ToString() != localIp)
                    {
                        return endPoint.Address.ToString();
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    break;
                }
            }
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            socket?.Dispose();
        }

        return null;
    }

    private static string? ReadArpNeighbor(int interfaceIndex, string localIp)
    {
        var size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return null;

        var mem = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(mem, ref size, false) != 0)
                return null;

            var count = Marshal.ReadInt32(mem);
            var row = IntPtr.Add(mem, 4);
            string? fallback = null;
            for (var i = 0; i < count; i++)
            {
                var index = Marshal.ReadInt32(row, 0);
                var physLen = Marshal.ReadInt32(row, 4);
                var type = Marshal.ReadInt32(row, 20);
                var ip = new IPAddress((uint)Marshal.ReadInt32(row, 16)).ToString();
                var sameAdapter = interfaceIndex == 0 || index == interfaceIndex;
                if (sameAdapter && type != 2 && physLen >= 6 && IsLinkLocal(ip) && ip != localIp && IsUsefulMac(row))
                {
                    if (type == 3)
                        return ip;
                    fallback ??= ip;
                }

                row = IntPtr.Add(row, 24);
            }

            return fallback;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private static bool IsUsefulMac(IntPtr row)
    {
        var allZero = true;
        Span<byte> mac = stackalloc byte[6];
        for (var i = 0; i < 6; i++)
        {
            mac[i] = Marshal.ReadByte(row, 8 + i);
            if (mac[i] != 0)
                allZero = false;
        }

        if (allZero)
            return false;
        if (mac[0] == 0xFF && mac[1] == 0xFF && mac[2] == 0xFF && mac[3] == 0xFF && mac[4] == 0xFF && mac[5] == 0xFF)
            return false;
        if (mac[0] == 0x01 && mac[1] == 0x00 && mac[2] == 0x5E)
            return false;
        return true;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    private static List<EnetAdapter> FindDiagAdapters(CableKind cable)
    {
        var found = new List<EnetAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (SkipHints.Any(hint => label.Contains(hint, StringComparison.Ordinal)))
                continue;

            var ethernet = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
            var wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var icom = cable == CableKind.Icom && label.Contains("icom", StringComparison.Ordinal);
            if (icom)
            {
                AddAdapterAddresses(nic, found, linkLocalOnly: false);
                continue;
            }

            if (!ethernet && !(cable == CableKind.Mhd && wireless))
                continue;

            AddAdapterAddresses(nic, found, linkLocalOnly: true);
        }

        return found;
    }

    private static void AddAdapterAddresses(NetworkInterface nic, List<EnetAdapter> found, bool linkLocalOnly)
    {
        foreach (var address in nic.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            var ip = address.Address.ToString();
            if (ip.StartsWith("127.", StringComparison.Ordinal))
                continue;
            if (linkLocalOnly && !IsLinkLocal(ip))
                continue;

            var index = 0;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties().Index;
            }
            catch
            {
                index = 0;
            }

            found.Add(new EnetAdapter(nic.Name, ip, index));
        }
    }

    private static IReadOnlyList<TechnicianAddress> ListTechnicianAddresses()
    {
        var found = new List<TechnicianAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var label = nic.Name + " " + nic.Description;
            var lower = label.ToLowerInvariant();
            if (lower.Contains("bluetooth", StringComparison.Ordinal) ||
                lower.Contains("vethernet", StringComparison.Ordinal) ||
                lower.Contains("virtualbox", StringComparison.Ordinal) ||
                lower.Contains("vmware", StringComparison.Ordinal) ||
                lower.Contains("wsl", StringComparison.Ordinal) ||
                lower.Contains("isatap", StringComparison.Ordinal) ||
                lower.Contains("teredo", StringComparison.Ordinal))
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var ip = address.Address.ToString();
                if (IsLinkLocal(ip) || ip.StartsWith("127.", StringComparison.Ordinal))
                    continue;

                var rank = 2;
                if (VpnHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal)))
                    rank = 0;
                else if (IsPrivateLan(ip))
                    rank = 1;

                found.Add(new TechnicianAddress(ip, nic.Name, rank));
            }
        }

        return found
            .GroupBy(item => item.Ip)
            .Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private static bool IsLocalAddress(string ip)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.ToString() == ip)
                    return true;
            }
        }

        return false;
    }

    private static bool IsPrivateLan(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;
        if (bytes[0] == 10)
            return true;
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;
        return false;
    }

    public static bool IsLinkLocal(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] != 169 || bytes[1] != 254)
            return false;
        if (bytes[2] == 255 && bytes[3] == 255)
            return false;
        if (bytes[2] == 0 && bytes[3] == 0)
            return false;
        return true;
    }

    private static string Failure(string lead, CommandResult result)
    {
        var detail = Flatten(result.Text);
        return string.IsNullOrWhiteSpace(detail) ? lead : lead + " " + detail;
    }

    private static string Flatten(string text)
        => string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private const int SIO_UDP_CONNRESET = -1744830452;

    private sealed record ProxyRow(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort);

    private readonly record struct CommandResult(int Code, string Stdout, string Stderr)
    {
        public string Text => (Stdout + " " + Stderr).Trim();
    }

    private sealed record EnetAdapter(string Name, string LocalIp, int Index);
}

public sealed class ScanResult
{
    public string CableState { get; init; } = "wait";
    public string CableDetail { get; init; } = "";
    public string PowerState { get; init; } = "wait";
    public string PowerDetail { get; init; } = "Waiting";
    public string VehicleTitle { get; init; } = "Car";
    public string VehicleState { get; init; } = "wait";
    public string VehicleDetail { get; init; } = "";
    public string? VehicleIp { get; init; }
    public string VehicleNote { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public string LocalEnetIp { get; init; } = "";
    public TechnicianAddress? BestTechnician { get; init; }
    public IReadOnlyList<TechnicianAddress> OtherTechnicians { get; init; } = Array.Empty<TechnicianAddress>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record TechnicianAddress(string Ip, string Name, int Rank);
