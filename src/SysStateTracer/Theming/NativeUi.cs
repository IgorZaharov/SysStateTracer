using System.Runtime.InteropServices;

namespace SysStateTracer.Theming;

/// <summary>Win32 calls used by the window code: dark title bars and scrollbars, window capture.</summary>
internal static partial class NativeUi
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmSystemBackdropType = 38;

    /// <summary>Dark or light title bar (Windows 10 2004+ and Windows 11; older builds use attribute 19).</summary>
    public static void SetDarkTitleBar(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }

    /// <summary>
    /// Mica behind the title bar (Windows 11 22H2, build 22621, and later, with the documented backdrop attribute).
    /// The window body stays opaque in Mica's own tint colours (<see cref="Palette.Surface"/>), so text and standard
    /// controls draw as before. Build 22000 is left with its plain title bar: its earlier, undocumented Mica switch
    /// drew the caption text on black boxes and let other windows show through the title bar. Windows 10 ignores it.
    /// </summary>
    public static void SetMica(nint hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22621)
        {
            return;
        }

        var mainWindow = 2; // DWMSBT_MAINWINDOW
        _ = DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref mainWindow, sizeof(int));
    }

    /// <summary>Explorer look for lists and trees; the dark variant also darkens their scrollbars.</summary>
    public static void SetExplorerTheme(nint hwnd, bool dark) =>
        _ = SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null);

    /// <summary>Copies a window, title bar included, as it is drawn on screen (used for screenshots in CI).</summary>
    public static bool PrintWindow(nint hwnd, nint hdc) => PrintWindowNative(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    [LibraryImport("user32.dll", EntryPoint = "PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindowNative(nint hwnd, nint hdc, uint flags);
}
