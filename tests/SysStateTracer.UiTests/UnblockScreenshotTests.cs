using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>
/// Screenshot for the README and the manual (F28): the Windows Properties window of a downloaded
/// SysStateTracer.exe, with "Unblock" ticked. The file gets the "downloaded from the internet" mark
/// (Zone.Identifier), Windows' own Properties window is opened for it, the box is ticked and the window is
/// saved, then closed with Cancel. Runs only when screenshots are enabled (CI).
/// </summary>
public class UnblockScreenshotTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int BmClick = 0x00F5;
    private const int WmCommand = 0x0111;
    private const int IdCancel = 2;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [Fact]
    public void DownloadedExe_PropertiesWithUnblockTicked()
    {
        if (Ui.ScreenshotFolder is not { } folder)
        {
            return;
        }

        Ui.Run(() =>
        {
            var downloads = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sst-" + Guid.NewGuid().ToString("N")[..8], "Downloads")).FullName;
            var exe = Path.Combine(downloads, "SysStateTracer.exe");
            var built = Path.Combine(AppContext.BaseDirectory, "SysStateTracer.exe");
            File.Copy(File.Exists(built) ? built : Path.Combine(Environment.SystemDirectory, "notepad.exe"), exe);
            File.WriteAllText(
                exe + ":Zone.Identifier",
                "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/IgorZaharov/SysStateTracer/releases/latest/download/SysStateTracer.exe\r\n");

            if (!SHObjectProperties(0, 2 /* SHOP_FILEPATH */, exe, null))
            {
                output.WriteLine("The Properties window could not be opened; no screenshot.");
                return;
            }

            nint sheet = 0;
            if (!Ui.PumpUntil(() => (sheet = FindOwnWindow("SysStateTracer.exe Properties")) != 0, 20_000))
            {
                output.WriteLine("The Properties window did not appear; no screenshot.");
                return;
            }

            Ui.Pump(1500);
            var unblock = FindChild(sheet, text => text.Replace("&", string.Empty, StringComparison.Ordinal).Contains("Unblock", StringComparison.OrdinalIgnoreCase));
            if (unblock == 0)
            {
                output.WriteLine("This Windows shows no Unblock box (attachment checks are off); no screenshot.");
                _ = SendMessage(sheet, WmCommand, IdCancel, 0);
                return;
            }

            _ = SendMessage(unblock, BmClick, 0, 0);
            Ui.Pump(600);
            Save(sheet, Path.Combine(folder, $"unblock-properties-{(ThemeManager.Current.IsDark ? "dark" : "light")}.png"));
            _ = SendMessage(sheet, WmCommand, IdCancel, 0); // nothing is changed on the copy
            Ui.Pump(300);
        });
    }

    private static void Save(nint hwnd, string path)
    {
        _ = GetWindowRect(hwnd, out var r);
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                _ = NativeUi.PrintWindow(hwnd, hdc);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static nint FindOwnWindow(string title)
    {
        var pid = (uint)Environment.ProcessId;
        nint found = 0;
        EnumWindows(
            (hwnd, unused) =>
            {
                _ = GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid && IsWindowVisible(hwnd) && Text(hwnd) == title)
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            0);
        return found;
    }

    private static nint FindChild(nint parent, Func<string, bool> match)
    {
        nint found = 0;
        EnumChildWindows(
            parent,
            (hwnd, unused) =>
            {
                var cls = new StringBuilder(64);
                _ = GetClassName(hwnd, cls, cls.Capacity);
                if (cls.ToString() == "Button" && match(Text(hwnd)))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            0);
        return found;
    }

    private static string Text(nint hwnd)
    {
        var text = new StringBuilder(256);
        _ = GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(nint hwnd, int type, string obj, string? page);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
