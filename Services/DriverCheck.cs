using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KdrEnet.Services;

internal static class DriverCheck
{
    public readonly record struct Hint(string Message, string Url);

    public static Hint? Find(CableKind cable)
    {
        foreach (var root in new[] { "USB", "FTDIBUS" })
        {
            RegistryKey? key;
            try
            {
                key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + root);
            }
            catch (Exception ex) when (IsLocked(ex))
            {
                continue;
            }

            var hint = Walk(key, root, cable, 0);
            if (hint is not null)
                return hint;
        }

        return null;
    }

    private static Hint? Walk(RegistryKey? key, string instanceId, CableKind cable, int depth)
    {
        if (key is null || depth > 4)
        {
            key?.Dispose();
            return null;
        }

        try
        {
            using (key)
            {
                if (NeedsDriver(instanceId, key))
                {
                    var hint = Match(instanceId + " " + ReadName(key), cable);
                    if (hint is not null)
                        return hint;
                }

                string[] names;
                try
                {
                    names = key.GetSubKeyNames();
                }
                catch (Exception ex) when (IsLocked(ex))
                {
                    return null;
                }

                foreach (var name in names)
                {
                    RegistryKey? child;
                    try
                    {
                        child = key.OpenSubKey(name);
                    }
                    catch (Exception ex) when (IsLocked(ex))
                    {
                        continue;
                    }

                    var hint = Walk(child, instanceId + "\\" + name, cable, depth + 1);
                    if (hint is not null)
                        return hint;
                }
            }
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            return null;
        }

        return null;
    }

    private static bool IsLocked(Exception ex)
        => ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;

    private static bool NeedsDriver(string instanceId, RegistryKey key)
    {
        if (key.GetValue("HardwareID") is null && key.GetValue("DeviceDesc") is null)
            return false;
        if (CM_Locate_DevNode(out var node, instanceId, 0) != 0)
            return false;
        if (CM_Get_DevNode_Status(out _, out var problem, node, 0) != 0)
            return false;
        return problem is 28 or 18 or 10 or 31;
    }

    private static string ReadName(RegistryKey key)
    {
        if (key.GetValue("FriendlyName") is string friendly && friendly.Length > 0)
            return friendly;
        return key.GetValue("DeviceDesc") as string ?? "";
    }

    private static Hint? Match(string blob, CableKind cable)
    {
        var text = blob.ToUpperInvariant();
        var ftdi = text.Contains("VID_0403", StringComparison.Ordinal) || text.Contains("FTDI", StringComparison.Ordinal);
        var ch340 = text.Contains("VID_1A86", StringComparison.Ordinal) || text.Contains("CH340", StringComparison.Ordinal) || text.Contains("CH341", StringComparison.Ordinal);
        var asix = text.Contains("VID_0B95", StringComparison.Ordinal) || text.Contains("ASIX", StringComparison.Ordinal) || text.Contains("AX88772", StringComparison.Ordinal);
        var realtek = text.Contains("VID_0BDA", StringComparison.Ordinal);
        var icom = text.Contains("ICOM", StringComparison.Ordinal);

        if (cable == CableKind.Kdcan && ftdi)
            return new Hint("This K+DCAN cable is in, but Windows has no driver for it.", "https://ftdichip.com/drivers/vcp-drivers/");
        if (cable == CableKind.Kdcan && ch340)
            return new Hint("This K+DCAN cable is in, but Windows has no driver for it.", "https://www.wch-ic.com/downloads/CH341SER_EXE.html");
        if ((cable == CableKind.Enet || cable == CableKind.Mhd) && asix)
            return new Hint("This ENET cable is in, but Windows has no driver for it.", "https://www.asix.com.tw/en/support/download");
        if ((cable == CableKind.Enet || cable == CableKind.Mhd) && realtek)
            return new Hint("This ENET cable is in, but Windows has no driver for it.", "https://www.realtek.com/en/downloads");
        if (cable == CableKind.Icom && icom)
            return new Hint("This ICOM is in, but Windows has no driver for it. Install the driver that came with the ICOM.", "");

        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);
}
