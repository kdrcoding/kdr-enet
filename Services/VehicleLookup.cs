using System.Net.Http;
using System.Text.Json;

namespace KdrEnet.Services;

/// <summary>
/// Fills in the model from the VIN. The cable reply has the VIN, not the model name.
/// A clean decode is shown. A failed or mismatched decode is left off.
/// </summary>
public static class VehicleLookup
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    public static async Task<string?> DescribeAsync(string vin, int? vinYear, CancellationToken ct = default)
    {
        if (vin.Length != 17)
            return null;

        using var response = await Http.GetAsync(
            "https://vpic.nhtsa.dot.gov/api/vehicles/DecodeVinValues/" + vin + "?format=json",
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("Results", out var results) || results.GetArrayLength() == 0)
            return null;

        var row = results[0];
        var error = Text(row, "ErrorCode");
        if (error != "0")
            return null;

        if (!int.TryParse(Text(row, "ModelYear"), out var year))
            return null;
        if (vinYear is int expected && year != expected)
            return null;

        var localMake = VehicleReader.Describe(vin).Make;
        var make = Clean(Text(row, "Make"));
        if (!SameMake(localMake, make))
            return null;
        var model = Clean(Text(row, "Model"));
        var series = Clean(Text(row, "Series"));
        if (string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(series))
            model = series;
        else if (!string.IsNullOrEmpty(series) && !string.IsNullOrEmpty(model) && series.Any(char.IsDigit) && !model.Any(char.IsDigit))
            model = series;

        var body = Clean(Text(row, "BodyClass"));
        if (!string.IsNullOrEmpty(body) && body.Equals(model, StringComparison.OrdinalIgnoreCase))
            body = "";

        var parts = new List<string> { year.ToString() };
        if (!string.IsNullOrEmpty(make))
            parts.Add(make);
        if (!string.IsNullOrEmpty(model))
            parts.Add(model);
        if (!string.IsNullOrEmpty(body))
            parts.Add(body);
        return parts.Count >= 2 ? string.Join(" · ", parts) : null;
    }

    private static bool SameMake(string local, string remote)
    {
        if (local.Length == 0 || remote.Length == 0)
            return false;
        if (local.Equals(remote, StringComparison.OrdinalIgnoreCase))
            return true;
        var localRoot = local.Split(' ')[0];
        var remoteRoot = remote.Split(' ')[0];
        return localRoot.Equals(remoteRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static string Clean(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return "";
        if (text.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase))
            return "";
        if (text.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            return "";
        return text;
    }
}
