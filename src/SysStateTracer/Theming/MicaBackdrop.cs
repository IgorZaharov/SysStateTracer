using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysStateTracer.Core.Config;

namespace SysStateTracer.Theming;

/// <summary>
/// Mica behind the whole window, as the Settings app has it, painted by SysStateTracer itself: Windows only puts
/// Mica behind the title bar of a Windows Forms window, and anything drawn on top of real Mica in the window body
/// turns see-through. Like Mica, it is the desktop wallpaper, blurred, given the tint's lightness and covered
/// by the tint (dark: #202020 at 80 %, light: #F3F3F3 at 50 %), fixed to the screen so that it stays put when the
/// window moves. One picture per screen is kept, as large as the screen; it is made again when the wallpaper,
/// the screens or the theme change. Off where Windows turns Mica off too: before Windows 11 22H2, with
/// transparency effects off, in high contrast and over Remote Desktop.
/// </summary>
internal static class MicaBackdrop
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Dictionary<string, Bitmap> Pictures = [];
    private static bool? _available;
    private static bool _picturesDark;
    private static Color? _average;
    private static bool _listening;

    /// <summary>Raised (on any thread) when the wallpaper, the screens or the transparency setting change.</summary>
    public static event EventHandler? Changed;

    /// <summary>Tests: the backdrop on (true) or off (false) whatever Windows does; null follows Windows.</summary>
    internal static bool? Override { get; set; }

    /// <summary>Tests: a wallpaper of their own instead of the desktop's.</summary>
    internal static Func<Wallpaper>? WallpaperSource { get; set; }

    /// <summary>Whether the backdrop is shown on this PC now.</summary>
    public static bool Available
    {
        get
        {
            Listen();
            return Override ?? (_available ??= Environment.OSVersion.Version.Build >= 22621
                && !SystemInformation.HighContrast
                && !SystemInformation.TerminalServerSession
                && TransparencyEffectsOn());
        }
    }

    /// <summary>The backdrop's average colour on the primary screen: the window body's colour where it is drawn solid.</summary>
    public static Color Average(bool dark)
    {
        if (_average is { } average && _picturesDark == dark)
        {
            return average;
        }

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _ = Picture(screen, dark);
        return _average ?? (dark ? Tints.Dark : Tints.Light);
    }

    /// <summary>
    /// Draws the backdrop for <paramref name="clip"/> (in <paramref name="control"/>'s client coordinates) at the
    /// place on the screen that part of the control is at.
    /// </summary>
    public static void Paint(Graphics g, Control control, Rectangle clip, bool dark)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(control);
        if (clip.IsEmpty || !control.IsHandleCreated)
        {
            return;
        }

        var origin = control.PointToScreen(Point.Empty);
        var area = new Rectangle(origin.X + clip.X, origin.Y + clip.Y, clip.Width, clip.Height);
        var state = g.Save();
        try
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var screen in Screen.AllScreens)
            {
                var part = Rectangle.Intersect(area, screen.Bounds);
                if (part.IsEmpty || Picture(screen, dark) is not { } picture)
                {
                    continue;
                }

                var target = new Rectangle(part.X - origin.X, part.Y - origin.Y, part.Width, part.Height);
                g.DrawImage(picture, target, part.X - screen.Bounds.X, part.Y - screen.Bounds.Y, part.Width, part.Height, GraphicsUnit.Pixel);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>Forgets the pictures and the settings read, so that they are made again when next needed.</summary>
    public static void Reset()
    {
        lock (Pictures)
        {
            foreach (var picture in Pictures.Values)
            {
                picture.Dispose();
            }

            Pictures.Clear();
            _available = null;
            _average = null;
        }
    }

    private static void Listen()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
            {
                Reset();
                Changed?.Invoke(null, EventArgs.Empty);
            }
        };
        SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            Reset();
            Changed?.Invoke(null, EventArgs.Empty);
        };
    }

    private static bool TransparencyEffectsOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("EnableTransparency") is not int on || on != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static Bitmap? Picture(Screen screen, bool dark)
    {
        lock (Pictures)
        {
            if (_picturesDark != dark)
            {
                foreach (var old in Pictures.Values)
                {
                    old.Dispose();
                }

                Pictures.Clear();
                _average = null;
                _picturesDark = dark;
            }

            if (Pictures.TryGetValue(screen.DeviceName, out var picture))
            {
                return picture;
            }

            if (screen.Bounds.Width <= 0 || screen.Bounds.Height <= 0)
            {
                return null;
            }

            try
            {
                using var wallpaper = (WallpaperSource ?? Wallpaper.Load)();
                picture = Make(screen.Bounds.Size, dark, wallpaper, out var average);
                Pictures[screen.DeviceName] = picture;
                if (screen.Primary || _average is null)
                {
                    _average = average;
                }

                return picture;
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or OutOfMemoryException or InvalidOperationException)
            {
                Log.Warning("The window backdrop could not be made", ex);
                return null;
            }
        }
    }

    /// <summary>The backdrop for one screen: the wallpaper drawn small (which blurs it), smoothed, tinted, then enlarged.</summary>
    internal static Bitmap Make(Size screen, bool dark, Wallpaper? wallpaper, out Color average)
    {
        var width = Math.Max(8, screen.Width / 32);
        var height = Math.Max(8, screen.Height / 32);
        using var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.Clear(wallpaper?.Background ?? Color.Black);
            if (wallpaper?.Image is { } image)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, Wallpaper.Place(image.Size, screen, wallpaper.Style, new Size(width, height)));
            }
        }

        var bits = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var pixels = new int[width * height];
        try
        {
            Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            for (var pass = 0; pass < 3; pass++)
            {
                Blur(pixels, width, height, radius: 2);
            }

            average = Tint(pixels, dark);
            Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
        }
        finally
        {
            small.UnlockBits(bits);
        }

        var picture = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(picture))
        using (var edges = new ImageAttributes())
        {
            edges.SetWrapMode(WrapMode.TileFlipXY); // no darker rim where the small picture ends
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, new Rectangle(0, 0, screen.Width, screen.Height), 0, 0, width, height, GraphicsUnit.Pixel, edges);
        }

        return picture;
    }

    /// <summary>A box blur across and down, in place.</summary>
    private static void Blur(int[] pixels, int width, int height, int radius)
    {
        var copy = (int[])pixels.Clone();
        BlurLine(copy, pixels, width, height, radius, across: true);
        copy = (int[])pixels.Clone();
        BlurLine(copy, pixels, width, height, radius, across: false);
    }

    private static void BlurLine(int[] from, int[] to, int width, int height, int radius, bool across)
    {
        var lines = across ? height : width;
        var length = across ? width : height;
        for (var line = 0; line < lines; line++)
        {
            for (var i = 0; i < length; i++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (var k = Math.Max(0, i - radius); k <= Math.Min(length - 1, i + radius); k++)
                {
                    var c = from[across ? (line * width) + k : (k * width) + line];
                    r += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    n++;
                }

                to[across ? (line * width) + i : (i * width) + line] = unchecked((int)0xFF000000) | ((r / n) << 16) | ((g / n) << 8) | (b / n);
            }
        }
    }

    /// <summary>
    /// Mica's two layers over the blurred wallpaper: its colour at the tint's luminosity (the "luminosity" blend
    /// mode, which keeps the colour's chroma), then the tint itself. Returns the average result.
    /// </summary>
    private static Color Tint(int[] pixels, bool dark)
    {
        var tint = dark ? Tints.Dark : Tints.Light;
        var opacity = dark ? 0.8 : 0.5;
        var luminosity = Luminosity(tint.R / 255.0, tint.G / 255.0, tint.B / 255.0);
        long sumR = 0, sumG = 0, sumB = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = Color.FromArgb(pixels[i]);
            var (r, g, b) = WithLuminosity(c.R / 255.0, c.G / 255.0, c.B / 255.0, luminosity);
            r = (r * 255) + ((tint.R - (r * 255)) * opacity);
            g = (g * 255) + ((tint.G - (g * 255)) * opacity);
            b = (b * 255) + ((tint.B - (b * 255)) * opacity);
            var (ri, gi, bi) = (Math.Clamp((int)Math.Round(r), 0, 255), Math.Clamp((int)Math.Round(g), 0, 255), Math.Clamp((int)Math.Round(b), 0, 255));
            pixels[i] = unchecked((int)0xFF000000) | (ri << 16) | (gi << 8) | bi;
            sumR += ri;
            sumG += gi;
            sumB += bi;
        }

        var count = Math.Max(1, pixels.Length);
        return Color.FromArgb((int)(sumR / count), (int)(sumG / count), (int)(sumB / count));
    }

    private static double Luminosity(double r, double g, double b) => (0.3 * r) + (0.59 * g) + (0.11 * b);

    /// <summary>The colour moved to luminosity <paramref name="target"/>, kept in range (SetLum and ClipColor of the blend modes).</summary>
    private static (double R, double G, double B) WithLuminosity(double r, double g, double b, double target)
    {
        var d = target - Luminosity(r, g, b);
        r += d;
        g += d;
        b += d;
        var l = Luminosity(r, g, b);
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (min < 0 && l - min > 1e-9)
        {
            (r, g, b) = (l + ((r - l) * l / (l - min)), l + ((g - l) * l / (l - min)), l + ((b - l) * l / (l - min)));
        }

        if (max > 1 && max - l > 1e-9)
        {
            (r, g, b) = (l + ((r - l) * (1 - l) / (max - l)), l + ((g - l) * (1 - l) / (max - l)), l + ((b - l) * (1 - l) / (max - l)));
        }

        return (r, g, b);
    }

    /// <summary>Mica's tint colours.</summary>
    internal static class Tints
    {
        public static readonly Color Dark = Color.FromArgb(0x20, 0x20, 0x20);
        public static readonly Color Light = Color.FromArgb(0xF3, 0xF3, 0xF3);
    }
}

/// <summary>The desktop wallpaper: the picture, how it is placed, and the desktop colour around it.</summary>
internal sealed class Wallpaper : IDisposable
{
    private const string DesktopKey = @"Control Panel\Desktop";

    public Wallpaper(Image? image, WallpaperStyle style, Color background)
    {
        Image = image;
        Style = style;
        Background = background;
    }

    public Image? Image { get; }

    public WallpaperStyle Style { get; }

    public Color Background { get; }

    /// <summary>
    /// The current wallpaper: Windows' own copy of it (also for a slide show), or the file the settings name; no
    /// picture when the desktop is a plain colour or the file cannot be read.
    /// </summary>
    public static Wallpaper Load()
    {
        var style = WallpaperStyle.Fill;
        string? path = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            path = key?.GetValue("WallPaper") as string;
            style = (key?.GetValue("WallpaperStyle") as string, key?.GetValue("TileWallpaper") as string) switch
            {
                (_, "1") => WallpaperStyle.Tile,
                ("0", _) => WallpaperStyle.Center,
                ("2", _) => WallpaperStyle.Stretch,
                ("6", _) => WallpaperStyle.Fit,
                _ => WallpaperStyle.Fill,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Plain colour then.
        }

        var transcoded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        var image = string.IsNullOrEmpty(path) ? null : Read(transcoded) ?? Read(path);
        return new Wallpaper(image, style, DesktopColour());
    }

    /// <summary>Where the wallpaper goes on a screen of <paramref name="screen"/> pixels, drawn on a picture of <paramref name="target"/> pixels.</summary>
    public static RectangleF Place(Size image, Size screen, WallpaperStyle style, Size target)
    {
        var scaleX = (float)target.Width / screen.Width;
        var scaleY = (float)target.Height / screen.Height;
        float width, height;
        switch (style)
        {
            case WallpaperStyle.Stretch:
                return new RectangleF(0, 0, target.Width, target.Height);
            case WallpaperStyle.Center:
                width = image.Width * scaleX;
                height = image.Height * scaleY;
                break;
            default:
                var fit = style == WallpaperStyle.Fit
                    ? Math.Min((float)screen.Width / image.Width, (float)screen.Height / image.Height)
                    : Math.Max((float)screen.Width / image.Width, (float)screen.Height / image.Height); // Fill, Tile and Span as Fill
                width = image.Width * fit * scaleX;
                height = image.Height * fit * scaleY;
                break;
        }

        return new RectangleF((target.Width - width) / 2, (target.Height - height) / 2, width, height);
    }

    public void Dispose() => Image?.Dispose();

    private static Image? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Read into memory: Image.FromFile keeps the file locked while the picture lives.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var decoded = Image.FromStream(stream);
            return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static Color DesktopColour()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            if (key?.GetValue("Background") is string rgb && rgb.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var r, var g, var b]
                && byte.TryParse(r, out var red) && byte.TryParse(g, out var green) && byte.TryParse(b, out var blue))
            {
                return Color.FromArgb(red, green, blue);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Black then.
        }

        return Color.Black;
    }
}

/// <summary>How the wallpaper is placed on the screen (Settings → Personalization → Background).</summary>
internal enum WallpaperStyle
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile,
}
