using Microsoft.Win32;

namespace KdrEnet.Services;

internal static class UsbCableProbe
{
    public static string? FindKdcanPort()
    {
        string? weak = null;
        foreach (var (port, name) in ListPorts())
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("bluetooth", StringComparison.Ordinal) ||
                lower.Contains("communications port", StringComparison.Ordinal))
                continue;

            if (lower.Contains("dcan", StringComparison.Ordinal) ||
                lower.Contains("k+dcan", StringComparison.Ordinal) ||
                lower.Contains("k-dcan", StringComparison.Ordinal))
                return Short(name) + " · " + port;

            if (weak is null &&
                (lower.Contains("ftdi", StringComparison.Ordinal) ||
                 lower.Contains("usb serial", StringComparison.Ordinal) ||
                 lower.Contains("ch340", StringComparison.Ordinal) ||
                 lower.Contains("can", StringComparison.Ordinal)))
                weak = Short(name) + " · " + port;
        }

        return weak;
    }

    private static string Short(string name)
    {
        var cut = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        var trimmed = cut > 0 ? name[..cut].Trim() : name.Trim();
        return trimmed.Length > 28 ? trimmed[..28] : trimmed;
    }

    private static List<(string Port, string Name)> ListPorts()
    {
        var ports = new List<string>();
        using (var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
        {
            if (map is not null)
            {
                foreach (var valueName in map.GetValueNames())
                {
                    if (map.GetValue(valueName) is string com && com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        ports.Add(com);
                }
            }
        }

        var friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Collect(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS"), friendly, 0);
        Collect(Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"), friendly, 0);

        var named = new List<(string, string)>();
        foreach (var port in ports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            friendly.TryGetValue(port, out var name);
            named.Add((port, name ?? port));
        }

        return named;
    }

    private static void Collect(RegistryKey? key, Dictionary<string, string> into, int depth)
    {
        if (key is null || depth > 5)
        {
            key?.Dispose();
            return;
        }

        try
        {
            using (key)
            {
                if (key.GetValue("FriendlyName") is string name)
                {
                    var open = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                    var close = open >= 0 ? name.IndexOf(')', open) : -1;
                    if (close > open)
                    {
                        var port = name[(open + 1)..close];
                        into[port] = name;
                    }
                }

                foreach (var child in key.GetSubKeyNames())
                {
                    try
                    {
                        Collect(key.OpenSubKey(child), into, depth + 1);
                    }
                    catch
                    {
                        // One unreadable device key should not hide the cable.
                    }
                }
            }
        }
        catch
        {
            // Windows locks some device keys even for an administrator.
        }
    }
}
