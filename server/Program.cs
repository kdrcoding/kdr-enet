using System.Net;
using KdrEnet.Relay;

var port = 7601;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
        port = parsed;
}

Console.WriteLine("KDR ENET session server listening on " + port + ".");
Console.WriteLine("Both laptops connect outward. A matching 6-digit code joins them.");
await new RelayHub().RunAsync(IPAddress.Any, port, CancellationToken.None);
