using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MHassnage.Wpf.View;

/// <summary>Barre de titre Windows en thème sombre (Windows 10 20H1+ / 11).</summary>
internal static partial class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        int on = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
        // Windows 11 : barre de la couleur du fond de l'application (#1E2228, format 0x00BBGGRR)
        int color = 0x0028221E;
        DwmSetWindowAttribute(handle, CaptionColor, ref color, sizeof(int));
    }
}
