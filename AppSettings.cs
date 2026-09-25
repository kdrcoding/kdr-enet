using System.IO;

namespace KdrEnet;

public enum CableKind
{
    Enet,
    Kdcan,
    Icom,
    Mhd
}

public enum VehicleKind
{
    Car,
    Bike
}

public static class AppSettings
{
    public static CableKind Cable { get; private set; } = CableKind.Enet;
    public static VehicleKind Vehicle { get; private set; } = VehicleKind.Car;

    public static string CableLabel => Cable switch
    {
        CableKind.Kdcan => "K+DCAN",
        CableKind.Icom => "ICOM",
        CableKind.Mhd => "MHD",
        _ => "ENET"
    };

    public static string VehicleLabel => Vehicle == VehicleKind.Bike ? "Bike" : "Car";

    public static string VehicleWord => Vehicle == VehicleKind.Bike ? "bike" : "car";

    public static string PlugSentence => Cable switch
    {
        CableKind.Kdcan => "Plug the K+DCAN USB cable into this laptop.",
        CableKind.Icom => "Plug the ICOM into this laptop.",
        CableKind.Mhd => "Connect the MHD cable or its adapter.",
        _ => "Plug the ENET cable into this laptop."
    };

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KDR Coding", "KdrEnet");

    private static string PathToFile => Path.Combine(Folder, "settings.txt");

    public static void Load()
    {
        try
        {
            if (!File.Exists(PathToFile))
                return;
            foreach (var raw in File.ReadAllLines(PathToFile))
            {
                var line = raw.Trim();
                var cut = line.IndexOf('=');
                if (cut <= 0)
                    continue;
                var key = line[..cut].Trim();
                var value = line[(cut + 1)..].Trim();
                if (key == "cable" && Enum.TryParse<CableKind>(value, ignoreCase: true, out var cable))
                    Cable = cable;
                if (key == "vehicle" && Enum.TryParse<VehicleKind>(value, ignoreCase: true, out var vehicle))
                    Vehicle = vehicle;
            }
        }
        catch
        {
            Cable = CableKind.Enet;
            Vehicle = VehicleKind.Car;
        }
    }

    public static void Save(CableKind cable, VehicleKind vehicle)
    {
        Cable = cable;
        Vehicle = vehicle;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathToFile, "cable=" + cable + Environment.NewLine + "vehicle=" + vehicle + Environment.NewLine);
    }
}
