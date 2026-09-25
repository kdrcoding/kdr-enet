using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KdrEnet;

internal static class DarkTitle
{
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var caption = 0x000D0705;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        var text = 0x00FBF7F4;
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
