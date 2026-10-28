using SysStateTracer.Core.Config;

namespace SysStateTracer;

/// <summary>
/// Remembers each window's size, position and maximized state in SysStateTracer.config (F12). A window
/// comes back where it was closed, unless that place is no longer on any screen (a monitor was removed);
/// then it keeps its default size and position. Dialogs that open centred on their owner keep that, and
/// only their size comes back.
/// </summary>
internal static class WindowLayout
{
    /// <summary>Part of the window that must stay on a screen for the saved position to be used.</summary>
    private const int MinVisible = 120;

    public static void Remember(Form form, string name, AppPaths paths, AppSettings settings, bool keepPosition = true)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        form.Load += (_, _) => Restore(form, name, settings, keepPosition);
        form.FormClosed += (_, _) =>
        {
            Store(form, name, settings);
            try
            {
                SettingsStore.Save(paths.ConfigFile, settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("The window size could not be saved", ex);
            }
        };
    }

    internal static void Restore(Form form, string name, AppSettings settings, bool keepPosition)
    {
        if (!settings.Windows.TryGetValue(name, out var saved) || saved.Width <= 0 || saved.Height <= 0)
        {
            return;
        }

        var size = new Size(Math.Max(saved.Width, form.MinimumSize.Width), Math.Max(saved.Height, form.MinimumSize.Height));
        var bounds = new Rectangle(new Point(saved.X, saved.Y), size);
        var screen = Screen.FromRectangle(bounds).WorkingArea;
        size = new Size(Math.Min(size.Width, screen.Width), Math.Min(size.Height, screen.Height));
        if (keepPosition && OnScreen(bounds))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(bounds.Location, size);
        }
        else
        {
            form.Size = size;
        }

        if (saved.Maximized && form.MaximizeBox)
        {
            form.WindowState = FormWindowState.Maximized;
        }
    }

    internal static void Store(Form form, string name, AppSettings settings)
    {
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        settings.Windows[name] = new WindowPlacement
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = form.WindowState == FormWindowState.Maximized,
        };
    }

    private static bool OnScreen(Rectangle bounds) => Screen.AllScreens.Any(s =>
    {
        var visible = Rectangle.Intersect(s.WorkingArea, bounds);
        return visible.Width >= MinVisible && visible.Height >= Math.Min(MinVisible, bounds.Height);
    });
}
