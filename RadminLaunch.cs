using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KdrEnet;

internal static class RadminLaunch
{
    public const string InstallFolder = @"C:\Program Files (x86)\Radmin VPN";
    public const string Portal = "https://www.radmin-vpn.com/";

    public static string? ExePath()
    {
        var vpn = Path.Combine(InstallFolder, "RvRvpnGui.exe");
        if (File.Exists(vpn))
            return vpn;

        var other = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Radmin VPN", "RvRvpnGui.exe");
        return File.Exists(other) ? other : null;
    }

    public static string? VpnIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (!label.Contains("radmin", StringComparison.Ordinal))
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var ip = address.Address.ToString();
                if (ip.StartsWith("26.", StringComparison.Ordinal))
                    return ip;
            }
        }

        return null;
    }

    public static void Open()
    {
        var path = ExePath() ?? throw new InvalidOperationException(
            "Radmin VPN is not in " + InstallFolder + ".");

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)
        });
    }
}
