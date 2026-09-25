using System.IO;

namespace KdrEnet.Services;

internal static class RelaySettings
{
    public const int DefaultPort = 7601;

    public static bool TryGet(out string host, out int port)
    {
        host = "";
        port = DefaultPort;

        var env = Environment.GetEnvironmentVariable("KDR_ENET_RELAY");
        if (!string.IsNullOrWhiteSpace(env) && TryParse(env.Trim(), out host, out port))
            return true;

        foreach (var path in Candidates())
        {
            if (!File.Exists(path))
                continue;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                if (TryParse(line, out host, out port))
                    return true;
            }
        }

        return false;
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KDR Coding",
        "KdrEnet",
        "relay-host.txt");

    private static IEnumerable<string> Candidates()
    {
        yield return SettingsPath;
        yield return Path.Combine(AppContext.BaseDirectory, "relay-host.txt");
    }

    private static bool TryParse(string line, out string host, out int port)
    {
        host = "";
        port = DefaultPort;
        var cut = line.LastIndexOf(':');
        if (cut > 0 && int.TryParse(line[(cut + 1)..], out var parsed) && parsed is > 0 and <= 65535)
        {
            host = line[..cut].Trim();
            port = parsed;
        }
        else
        {
            host = line.Trim();
        }

        return host.Length > 0 && !host.Contains(' ');
    }
}
