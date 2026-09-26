using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace KdrEnet.Services;

/// <summary>
/// Looks at the public GitHub release and replaces this exe when a newer one is there.
/// The download address has to be this repo's release file. A live session is left alone.
/// </summary>
public static class AppUpdate
{
    public readonly record struct Offer(Version Version, Uri Url);

    private const string Api = "https://api.github.com/repos/kdrcoding/kdr-enet/releases/latest";
    private const string ReleasePrefix = "/kdrcoding/kdr-enet/releases/download/";

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KDR Coding", "KdrEnet", "update");

    public static Version Current
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return Normalize(version);
        }
    }

    public static string Text(Version version) => version.Major + "." + version.Minor + "." + Math.Max(version.Build, 0);

    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        if (!Version.TryParse(text, out var parsed))
            return false;
        version = Normalize(parsed);
        return version > new Version(0, 0);
    }

    public static Offer? ReadOffer(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) || !TryParseTag(tag.GetString(), out var version))
            return null;
        if (!IsNewer(version, current))
            return null;
        if (!root.TryGetProperty("assets", out var assets))
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != "KdrEnet.exe")
                continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlText))
                continue;
            if (!Uri.TryCreate(urlText.GetString(), UriKind.Absolute, out var url))
                continue;
            if (!IsReleaseUrl(url))
                continue;
            return new Offer(version, url);
        }

        return null;
    }

    public static async Task<Offer?> FindAsync(CancellationToken ct = default)
    {
        using var client = Client(TimeSpan.FromSeconds(8));
        using var response = await client.GetAsync(Api, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        return ReadOffer(json, Current);
    }

    public static async Task<string> DownloadAsync(Uri url, string folder, CancellationToken ct = default)
    {
        if (!IsReleaseUrl(url))
            throw new InvalidOperationException("The new version did not download. This one keeps running.");

        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "KdrEnet.download");
        var finalPath = Path.Combine(folder, "KdrEnet.exe");
        using var client = Client(TimeSpan.FromMinutes(5));
        using var response = await FollowAsync(client, url, ct);
        if (response.Content.Headers.ContentLength is long advertised && !SaneSize(advertised))
            throw new InvalidOperationException("The new version did not download. This one keeps running.");

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, ct);
        }

        var length = new FileInfo(temp).Length;
        if (!SaneSize(length) || !StartsWithExe(temp))
        {
            TryDelete(temp);
            throw new InvalidOperationException("The new version did not download. This one keeps running.");
        }

        File.Move(temp, finalPath, overwrite: true);
        return finalPath;
    }

    public static void StartSwap(string downloadedExe, int processId, string? currentExe)
    {
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(downloadedExe))
            throw new InvalidOperationException("This copy could not be replaced. Download it from GitHub.");

        var script = Path.Combine(Path.GetDirectoryName(downloadedExe)!, "apply.ps1");
        File.WriteAllText(script, Script(processId, downloadedExe, currentExe));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }.WithArguments("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script));
    }

    public static bool CheckSample()
    {
        if (!TryParseTag("v2.8.0", out var parsed) || parsed != new Version(2, 8, 0))
            return false;
        if (!TryParseTag("2.8", out var shortTag) || shortTag != new Version(2, 8, 0))
            return false;
        if (IsNewer(new Version(2, 7, 0), new Version(2, 8, 0)))
            return false;
        if (!IsNewer(new Version(2, 8, 0), new Version(2, 7, 0)))
            return false;
        if (IsNewer(new Version(2, 8, 0), new Version(2, 8, 0)))
            return false;

        const string good = """
            {"tag_name":"v2.8.0","assets":[{"name":"KdrEnet.exe","browser_download_url":"https://github.com/kdrcoding/kdr-enet/releases/download/v2.8.0/KdrEnet.exe"}]}
            """;
        if (ReadOffer(good, new Version(2, 7, 0)) is not Offer offer || offer.Version != new Version(2, 8, 0))
            return false;
        if (ReadOffer(good, new Version(2, 8, 0)) is not null)
            return false;

        const string otherSite = """
            {"tag_name":"v9.0.0","assets":[{"name":"KdrEnet.exe","browser_download_url":"https://evil.example/KdrEnet.exe"}]}
            """;
        if (ReadOffer(otherSite, new Version(2, 7, 0)) is not null)
            return false;

        const string otherFile = """
            {"tag_name":"v9.0.0","assets":[{"name":"other.exe","browser_download_url":"https://github.com/kdrcoding/kdr-enet/releases/download/v9.0.0/other.exe"}]}
            """;
        return ReadOffer(otherFile, new Version(2, 7, 0)) is null;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static bool SaneSize(long length) => length is >= 8_000_000 and <= 120_000_000;

    private static bool StartsWithExe(string path)
    {
        Span<byte> magic = stackalloc byte[2];
        using var stream = File.OpenRead(path);
        return stream.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
    }

    private static bool IsReleaseUrl(Uri url) =>
        url.IsAbsoluteUri &&
        url.Scheme == "https" &&
        url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.EndsWith("/KdrEnet.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsDownloadHop(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https")
            return false;
        if (IsReleaseUrl(url))
            return true;
        return url.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> FollowAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop < 5; hop++)
        {
            if (!IsDownloadHop(current))
                throw new InvalidOperationException("The new version did not download. This one keeps running.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri next)
            {
                var ahead = next.IsAbsoluteUri ? next : new Uri(current, next);
                response.Dispose();
                current = ahead;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new InvalidOperationException("The new version did not download. This one keeps running.");
            }

            return response;
        }

        throw new InvalidOperationException("The new version did not download. This one keeps running.");
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KdrEnet");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string Script(int processId, string source, string destination)
    {
        var src = Ps(source);
        var dest = Ps(destination);
        var self = Ps(Path.Combine(Path.GetDirectoryName(source)!, "apply.ps1"));
        return "$ErrorActionPreference = 'Stop'\r\n" +
               "$target = " + processId + "\r\n" +
               "$src = " + src + "\r\n" +
               "$dest = " + dest + "\r\n" +
               "for ($i = 0; $i -lt 40; $i++) {\r\n" +
               "    if (-not (Get-Process -Id $target -ErrorAction SilentlyContinue)) { break }\r\n" +
               "    Start-Sleep -Seconds 1\r\n" +
               "}\r\n" +
               "$copied = $false\r\n" +
               "for ($i = 0; $i -lt 8; $i++) {\r\n" +
               "    try {\r\n" +
               "        Copy-Item -LiteralPath $src -Destination $dest -Force\r\n" +
               "        $copied = $true\r\n" +
               "        break\r\n" +
               "    } catch { Start-Sleep -Seconds 1 }\r\n" +
               "}\r\n" +
               "if (Test-Path -LiteralPath $dest) { Start-Process -FilePath $dest }\r\n" +
               "if ($copied) { Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue }\r\n" +
               "Remove-Item -LiteralPath " + self + " -Force -ErrorAction SilentlyContinue\r\n";
    }

    private static string Ps(string path) => "'" + path.Replace("'", "''") + "'";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* the next download overwrites it */ }
    }

    private static ProcessStartInfo WithArguments(this ProcessStartInfo info, params string[] args)
    {
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }
}
