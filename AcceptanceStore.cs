using System.IO;

namespace KdrEnet;

public static class AcceptanceStore
{
    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KDR Coding", "KdrEnet");

    private static string PathToFile => Path.Combine(Folder, "terms-accepted.txt");

    public static bool IsAccepted()
    {
        try
        {
            return File.Exists(PathToFile) &&
                   File.ReadAllText(PathToFile).Trim() == LegalCopy.TermsVersion;
        }
        catch
        {
            return false;
        }
    }

    public static void Accept()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathToFile, LegalCopy.TermsVersion);
    }
}
