using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KdrEnet;

internal static class RadminLaunch
{
    public static string? ExePath()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var path = Path.Combine(root, "Radmin VPN", "Radmin.exe");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public const string Portal = "https://www.radmin-vpn.com/";

    public static string? VpnIp()
    {
        string? any = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var ip = address.Address.ToString();
                if (!ip.StartsWith("26.", StringComparison.Ordinal))
                    continue;
                if (label.Contains("radmin", StringComparison.Ordinal))
                    return ip;
                any ??= ip;
            }
        }

        return any;
    }

    public static void Open()
    {
        var path = ExePath() ?? throw new InvalidOperationException(
            "Radmin VPN is not installed on this laptop. Install it, open it on both laptops, and join the same network.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string Describe(bool haveAddress)
    {
        if (ExePath() is null)
            return "Install Radmin VPN on both laptops and join the same network. This program passes the car through that network. It does not create the network.";

        return haveAddress
            ? "Radmin is up on this laptop. Open Radmin on the other laptop, join this same network, and paste the copied address into E-Sys."
            : "Open Radmin VPN here and on the other laptop, then join the same network. The address for E-Sys shows up after this laptop is in that network.";
    }
}
