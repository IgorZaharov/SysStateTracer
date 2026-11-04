using System.Drawing.Drawing2D;
using SysStateTracer.Controls;
using SysStateTracer.Core.Config;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>The wallpaper backdrop behind the whole window (Mica as in Settings), painted by the app itself.</summary>
public class MicaBackdropTests
{
    [Fact]
    public void Backdrop_TakesTheWallpapersColour_AtMicasLightness()
    {
        using var blue = Wallpaper(Color.FromArgb(20, 60, 200), Color.FromArgb(10, 30, 120));

        using var dark = MicaBackdrop.Make(new Size(640, 360), dark: true, blue, out var darkAverage);
        using var light = MicaBackdrop.Make(new Size(640, 360), dark: false, blue, out var lightAverage);

        Assert.Equal(new Size(640, 360), dark.Size);
        Assert.True(darkAverage.B > darkAverage.R + 12, $"dark backdrop {darkAverage} should be bluish");
        Assert.InRange(Luminosity(darkAverage), 0.10, 0.16); // as dark as Mica's #202020
        Assert.True(lightAverage.B > lightAverage.R, $"light backdrop {lightAverage} should be bluish");
        Assert.InRange(Luminosity(lightAverage), 0.88, 0.97); // as light as Mica's #F3F3F3

        // Without a picture (a plain black desktop) it is the tint itself.
        using var plain = new Wallpaper(null, WallpaperStyle.Fill, Color.Black);
        using var tint = MicaBackdrop.Make(new Size(64, 64), dark: true, plain, out var plainAverage);
        Assert.Equal(MicaBackdrop.Tints.Dark.ToArgb(), plainAverage.ToArgb());
    }

    [Fact]
    public void Palette_OverBackdrop_CardsAndListsMatchTheTranslucentLayer()
    {
        var average = Color.FromArgb(30, 34, 48);

        var dark = ThemeManager.OverBackdrop(Palette.Dark, average);

        Assert.Equal(average, dark.Surface);
        Assert.Equal(Palette.Blend(average, Color.White, 0.05), dark.Background); // the card colour, solid
        Assert.Equal(13, ThemeManager.LayerOverBackdrop(dark).A); // 5 % white over the backdrop
        Assert.Equal(Palette.Dark.Text, dark.Text);
        Assert.Equal(179, ThemeManager.LayerOverBackdrop(ThemeManager.OverBackdrop(Palette.Light, average)).A);
    }

    [Fact]
    public void Wallpaper_Fill_CoversTheScreen_Fit_ShowsAllOfIt()
    {
        var fill = Theming.Wallpaper.Place(new Size(1000, 1000), new Size(1920, 1080), WallpaperStyle.Fill, new Size(60, 34));
        var fit = Theming.Wallpaper.Place(new Size(1000, 1000), new Size(1920, 1080), WallpaperStyle.Fit, new Size(60, 34));

        Assert.True(fill.Width >= 60 && fill.Height >= 34);
        Assert.True(fit.Height <= 34.01 && fit.Width < 60);
    }

    [Fact]
    public void Windows_OverTheBackdrop_ContainersSeeThrough_ListsSolid()
    {
        Ui.Run(() =>
        {
            MicaBackdrop.Override = true;
            MicaBackdrop.WallpaperSource = () => Wallpaper(Color.FromArgb(30, 80, 220), Color.FromArgb(120, 40, 200));
            MicaBackdrop.Reset();
            try
            {
                using var app = new TestApp();
                var before = app.AddSnapshot("Before", hoursAgo: 30);
                var after = app.AddSnapshot("After", hoursAgo: 29, installed: true);
                foreach (var preference in new[] { ThemePreference.Dark, ThemePreference.Light })
                {
                    ThemeManager.SetPreference(preference);
                    Assert.True(ThemeManager.MicaOn);
                    Assert.NotEqual(preference == ThemePreference.Dark ? Palette.Dark.Surface : Palette.Light.Surface, ThemeManager.Current.Surface);

                    using (var main = Ui.Show(new MainForm(app.Paths, app.Settings)))
                    {
                        main.Activate();
                        Ui.Pump(300);
                        Assert.Equal(ThemeManager.Current.Background, main.List.BackColor); // lists stay solid
                        Ui.Screenshot(main, "mica-main");
                        main.Close();
                    }

                    using (var preferences = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
                    {
                        preferences.Activate();
                        Ui.Pump(300);
                        Assert.Contains(AllControls(preferences), c => c is LayerPanel && c.BackColor == Color.Transparent);
                        Ui.Screenshot(preferences, "mica-preferences");

                        // Moving the window repaints the backdrop where the window now is.
                        preferences.Left += 40;
                        Ui.Pump(200);
                        preferences.Close();
                    }

                    using (var comparison = Ui.Show(new ComparisonForm(app.Paths, app.Settings, before, after)))
                    {
                        Assert.True(Ui.PumpUntil(() => comparison.Diff is not null && !comparison.IsLoading, 30_000), "comparison did not load");
                        Assert.Equal(ThemeManager.Current.Background, comparison.Tree.BackColor);
                        Ui.Screenshot(comparison, "mica-comparison");
                        comparison.Close();
                    }
                }
            }
            finally
            {
                MicaBackdrop.Override = null;
                MicaBackdrop.WallpaperSource = null;
                MicaBackdrop.Reset();
                ThemeManager.SetPalette(Palette.Light);
            }
        });
    }

    private static double Luminosity(Color c) => ((0.3 * c.R) + (0.59 * c.G) + (0.11 * c.B)) / 255;

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var inner in AllControls(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>A two-colour diagonal wallpaper.</summary>
    private static Wallpaper Wallpaper(Color from, Color to)
    {
        var image = new Bitmap(400, 225);
        using (var g = Graphics.FromImage(image))
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 400, 225), from, to, 30f))
        {
            g.FillRectangle(brush, 0, 0, 400, 225);
        }

        return new Wallpaper(image, WallpaperStyle.Fill, Color.Black);
    }
}
