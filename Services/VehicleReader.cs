using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KdrEnet.Services;

/// <summary>
/// Reads the VIN the car sends on the ENET cable.
/// HSFZ answers on UDP 6811. DoIP answers on UDP 13400.
/// Year and make are taken from the VIN itself. The model name is not in that reply.
/// </summary>
public static class VehicleReader
{
    public readonly record struct Found(string? Ip, string Vin);
    public readonly record struct Facts(int? Year, string Make);

    public static Found? Read(string localIp, string? vehicleIp, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var first = TimeSpan.FromMilliseconds(Math.Max(250, budget.TotalMilliseconds * 0.55));
        var hsfz = ReadHsfz(localIp, vehicleIp, first);
        if (hsfz is Found hsfzHit && hsfzHit.Vin.Length == 17)
            return hsfz;

        var left = deadline - DateTime.UtcNow;
        if (left < TimeSpan.FromMilliseconds(200))
            left = TimeSpan.FromMilliseconds(200);
        var doip = ReadDoip(localIp, vehicleIp ?? (hsfz is Found seen ? seen.Ip : null), left);
        if (doip is Found doipHit && doipHit.Vin.Length == 17)
            return doip;
        return hsfz ?? doip;
    }

    public static string? ParseHsfz(byte[] buffer, int length)
    {
        if (length < 8)
            return null;
        var payloadLength = ReadBe32(buffer, 0);
        if (payloadLength <= 0 || payloadLength > 512)
            return null;
        if (ReadBe16(buffer, 4) != 0x0011)
            return null;
        if (6 + payloadLength > length)
            return null;
        var text = Encoding.ASCII.GetString(buffer, 6, payloadLength);
        return VinAfterMarker(text);
    }

    public static string? ParseDoip(byte[] buffer, int length)
    {
        if (length < 8 + 17)
            return null;
        if ((buffer[0] ^ 0xFF) != buffer[1])
            return null;
        if (buffer[0] is not (0x01 or 0x02 or 0x03))
            return null;
        if (ReadBe16(buffer, 2) != 0x0004)
            return null;
        var payloadLength = ReadBe32(buffer, 4);
        if (payloadLength < 17 || payloadLength > 64)
            return null;
        if (8 + payloadLength > length)
            return null;
        var vin = Encoding.ASCII.GetString(buffer, 8, 17);
        return IsVin(vin) ? vin : null;
    }

    public static Facts Describe(string vin)
    {
        if (!IsVin(vin))
            return new Facts(null, "");
        return new Facts(YearFromVin(vin), MakeFromVin(vin));
    }

    public static void CheckSample()
    {
        var hsfz = Convert.FromHexString("000000320011444941474144523130424d574d4143374346436343463837393343424d5756494e5742413558373333333246483735373334");
        if (ParseHsfz(hsfz, hsfz.Length) != "WBA5X73332FH75734")
            throw new InvalidOperationException("The HSFZ sample VIN did not parse.");
        var hsfzFacts = Describe("WBA5X73332FH75734");
        if (hsfzFacts.Year != 2002 || hsfzFacts.Make != "BMW")
            throw new InvalidOperationException("The HSFZ sample year or make did not decode.");

        var vin = Encoding.ASCII.GetBytes("WBAJE5C59KWW12345");
        var frame = new byte[8 + 32];
        frame[0] = 0x02;
        frame[1] = 0xFD;
        frame[2] = 0x00;
        frame[3] = 0x04;
        frame[7] = 32;
        vin.CopyTo(frame, 8);
        if (ParseDoip(frame, frame.Length) != "WBAJE5C59KWW12345")
            throw new InvalidOperationException("The DoIP sample VIN did not parse.");
        var doipFacts = Describe("WBAJE5C59KWW12345");
        if (doipFacts.Year != 2019 || doipFacts.Make != "BMW")
            throw new InvalidOperationException("The DoIP sample year or make did not decode.");
    }

    private static Found? ReadHsfz(string localIp, string? vehicleIp, TimeSpan timeout)
    {
        Socket? socket = null;
        try
        {
            socket = NewSocket();
            socket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 6811));
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            var probe = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x11 };
            Send(socket, probe, "169.254.255.255", 6811);
            if (!string.IsNullOrEmpty(vehicleIp))
                Send(socket, probe, vehicleIp, 6811);
            return Receive(socket, localIp, timeout, ParseHsfz);
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static Found? ReadDoip(string localIp, string? vehicleIp, TimeSpan timeout)
    {
        Socket? socket = null;
        try
        {
            socket = NewSocket();
            socket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            var probe = new byte[] { 0x02, 0xFD, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
            Send(socket, probe, "169.254.255.255", 13400);
            if (!string.IsNullOrEmpty(vehicleIp))
                Send(socket, probe, vehicleIp, 13400);
            return Receive(socket, localIp, timeout, ParseDoip);
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static Found? Receive(Socket socket, string localIp, TimeSpan timeout, Func<byte[], int, string?> parse)
    {
        var buffer = new byte[512];
        Found? seen = null;
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
                if (read <= 0 || remote is not IPEndPoint endPoint)
                    continue;
                var ip = endPoint.Address.ToString();
                if (!SessionService.IsLinkLocal(ip) || ip == localIp)
                    continue;
                seen ??= new Found(ip, "");
                var vin = parse(buffer, read);
                if (vin is not null)
                    return new Found(ip, vin);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                break;
            }
        }

        return seen;
    }

    private static Socket NewSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
        return socket;
    }

    private static void Send(Socket socket, byte[] probe, string ip, int port)
    {
        try
        {
            socket.SendTo(probe, new IPEndPoint(IPAddress.Parse(ip), port));
        }
        catch (SocketException)
        {
            // The other probe can still leave this adapter.
        }
    }

    private static string? VinAfterMarker(string text)
    {
        var at = text.IndexOf("BMWVIN", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 6 + 17 > text.Length)
            return null;
        var vin = text.Substring(at + 6, 17).ToUpperInvariant();
        return IsVin(vin) ? vin : null;
    }

    private static bool IsVin(string vin)
    {
        if (vin.Length != 17)
            return false;
        foreach (var character in vin)
        {
            if (character is >= '0' and <= '9')
                continue;
            if (character is (>= 'A' and <= 'Z') && character is not ('I' or 'O' or 'Q'))
                continue;
            return false;
        }

        return true;
    }

    private static int? YearFromVin(string vin)
    {
        var code = vin[9];
        if (code is >= '1' and <= '9')
            return 2000 + (code - '0');
        const string letters = "ABCDEFGHJKLMNPRSTVWXY";
        var index = letters.IndexOf(code);
        return index < 0 ? null : 2010 + index;
    }

    private static string MakeFromVin(string vin)
    {
        var wmi = vin[..3];
        return wmi switch
        {
            "WBA" or "4US" or "5UM" or "5UX" => "BMW",
            "WBS" or "5YM" => "BMW M",
            "WBY" => "BMW i",
            "WMW" or "WMZ" => "MINI",
            "SCA" => "Rolls-Royce",
            _ => ""
        };
    }

    private static int ReadBe32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    private static int ReadBe16(byte[] buffer, int offset)
        => (buffer[offset] << 8) | buffer[offset + 1];
}
