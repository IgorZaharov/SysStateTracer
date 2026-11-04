using System.Runtime.CompilerServices;
using Microsoft.Win32;
using SysStateTracer.Core.Config;

namespace SysStateTracer.Theming;

/// <summary>How a control is coloured beyond the defaults for its type.</summary>
internal enum ThemeRole
{
    Default,

    /// <summary>Secondary text: captions, column headers, counts.</summary>
    Secondary,

    /// <summary>Surface background: sidebar, bars, panels set apart from the content.</summary>
    Surface,

    /// <summary>Headings: semibold 11 pt.</summary>
    Heading,
}

/// <summary>Implemented by controls that paint themselves with the palette.</summary>
internal interface IThemed
{
    void ApplyTheme(Palette palette);
}

/// <summary>
/// Light / Dark / Follow Windows (plan Step 41). Every window registers with <see cref="Attach"/>; changing
/// the preference, or the Windows app mode while following it, recolours all open windows at once.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly ConditionalWeakTable<Control, StrongBox<ThemeRole>> Roles = new();
    private static ThemePreference _preference = ThemePreference.System;
    private static bool _listening;
    private static bool _micaAllowed;

    public static event EventHandler? Changed;

    public static ThemePreference Preference => _preference;

    public static Palette Current { get; private set; } = Palette.Light;

    /// <summary>
    /// The wallpaper backdrop (<see cref="MicaBackdrop"/>) is behind the window body and the cards: only with the
    /// theme from the settings (not with a palette forced by tests and screenshots) and where Windows shows Mica.
    /// </summary>
    public static bool MicaOn => _micaAllowed && MicaBackdrop.Available;

    /// <summary>Lines between rows and columns in lists and the comparison grid (Preferences → General).</summary>
    public static bool GridLines { get; private set; }

    /// <summary>Every other row shaded in lists and the comparison grid (Preferences → General).</summary>
    public static bool ZebraStripes { get; private set; }

    /// <summary>Sets grid lines and zebra striping and redraws open windows when either changes.</summary>
    public static void SetListStyle(bool gridLines, bool zebraStripes)
    {
        if (GridLines == gridLines && ZebraStripes == zebraStripes)
        {
            return;
        }

        GridLines = gridLines;
        ZebraStripes = zebraStripes;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Sets the preference (from Preferences) and recolours open windows when the result changes.</summary>
    public static void SetPreference(ThemePreference preference)
    {
        _preference = preference;
        _micaAllowed = true;
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _preference == ThemePreference.System)
                {
                    Update();
                }
            };

            // A new wallpaper or screen, or transparency effects turned on or off: new colours and backdrop.
            MicaBackdrop.Changed += (_, _) => Update(force: true);
        }

        Update();
    }

    /// <summary>Forces a palette (screenshots and tests); the wallpaper backdrop is left out.</summary>
    public static void SetPalette(Palette palette)
    {
        _micaAllowed = false;
        Current = palette;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The palette over the wallpaper backdrop: the window body in the backdrop's average colour; cards, lists and
    /// text boxes as Windows 11's translucent layers over it (dark: 5 % white, light: 70 % white), so the parts
    /// drawn solid match the cards drawn over the backdrop.
    /// </summary>
    internal static Palette OverBackdrop(Palette palette, Color average) => palette with
    {
        Surface = average,
        Background = Palette.Blend(average, Color.White, palette.IsDark ? 0.05 : 0.7),
        Input = palette.IsDark ? Palette.Blend(average, Color.White, 0.09) : Color.White,
        Border = Palette.Blend(average, palette.IsDark ? Color.White : Color.Black, palette.IsDark ? 0.1 : 0.09),
    };

    /// <summary>A layer (card) over the backdrop: <see cref="Palette.Background"/> as a translucent white.</summary>
    public static Color LayerOverBackdrop(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return palette.IsDark ? Color.FromArgb(13, 255, 255, 255) : Color.FromArgb(179, 255, 255, 255);
    }

    /// <summary>
    /// Clears a custom-drawn control to what is behind it: its parent's colour, or, where the parent is see-through
    /// (over the backdrop), whatever the parent shows there. Returns a solid colour close to it, for blending.
    /// </summary>
    public static Color ClearBehind(Graphics g, Control control, Color fallback)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(control);
        var behind = control.Parent?.BackColor ?? fallback;
        var seeThrough = behind.A < 255 || (control.Parent is Form && MicaOn);
        if (!seeThrough || control.Parent is null)
        {
            g.Clear(behind);
            return behind;
        }

        ButtonRenderer.DrawParentBackground(g, control.ClientRectangle, control);
        return Current.Surface;
    }

    /// <summary>The window body: the backdrop while the window is active (Mica turns plain in inactive windows).</summary>
    private static void PaintBackdrop(Form form, PaintEventArgs e)
    {
        if (MicaOn && IsActive(form))
        {
            MicaBackdrop.Paint(e.Graphics, form, e.ClipRectangle, Current.IsDark);
        }
    }

    /// <summary>The active window (tests that force the backdrop on treat every window as active).</summary>
    private static bool IsActive(Form form) => Form.ActiveForm == form || MicaBackdrop.Override == true;

    /// <summary>
    /// Repaints what shows the backdrop: the window, its see-through containers and what is drawn on them; not
    /// lists, trees and text boxes, which are solid. Used when the window moves (the backdrop stays with the
    /// screen) and when it becomes active or inactive.
    /// </summary>
    private static void InvalidateBackdrop(Control control)
    {
        control.Invalidate(invalidateChildren: false);
        foreach (Control child in control.Controls)
        {
            if (child.BackColor.A < 255)
            {
                InvalidateBackdrop(child);
            }
            else if (child is not (ListView or TreeView or TextBoxBase or ListBox or ComboBox or Controls.VirtualTreeGrid or SplitContainer or Controls.DetailsPanel))
            {
                child.Invalidate(invalidateChildren: false);
            }
        }
    }

    public static void SetRole(Control control, ThemeRole role)
    {
        Roles.AddOrUpdate(control, new StrongBox<ThemeRole>(role));
        if (role == ThemeRole.Heading)
        {
            control.Font = UiFonts.Heading;
        }
    }

    /// <summary>Colours the window now, and again on every theme change until it closes.</summary>
    public static void Attach(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        void OnChanged(object? sender, EventArgs e) => Post(form, () => Apply(form));

        Changed += OnChanged;
        form.FormClosed += (_, _) => Changed -= OnChanged;
        form.HandleCreated += (_, _) =>
        {
            NativeUi.SetDarkTitleBar(form.Handle, Current.IsDark);
            NativeUi.SetMica(form.Handle);
        };
        form.Paint += (_, e) => PaintBackdrop(form, e);
        form.Activated += (_, _) =>
        {
            if (MicaOn)
            {
                InvalidateBackdrop(form);
            }
        };
        form.Deactivate += (_, _) =>
        {
            if (MicaOn)
            {
                InvalidateBackdrop(form);
            }
        };
        form.Move += (_, _) =>
        {
            if (MicaOn && IsActive(form))
            {
                InvalidateBackdrop(form);
            }
        };
        Apply(form);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the control's thread. Theme changes can come from the Windows settings
    /// thread while a window is closing, so a window that is gone is skipped instead of failing.
    /// </summary>
    public static void Post(Control control, Action action)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            if (control.IsDisposed || control.Disposing || !control.IsHandleCreated)
            {
                return;
            }

            if (control.InvokeRequired)
            {
                control.BeginInvoke(() =>
                {
                    if (!control.IsDisposed)
                    {
                        action();
                    }
                });
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window closed in the meantime.
        }
    }

    /// <summary>Applies the current palette to a window and everything in it.</summary>
    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var palette = Current;
        root.SuspendLayout();
        try
        {
            ApplyTree(root, palette);
        }
        finally
        {
            root.ResumeLayout();
        }

        if (root is Form form && form.IsHandleCreated)
        {
            NativeUi.SetDarkTitleBar(form.Handle, palette.IsDark);
            form.Invalidate(invalidateChildren: true);
        }
    }

    /// <summary>True when Windows is set to dark mode for apps.</summary>
    public static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void Update(bool force = false)
    {
        var dark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => WindowsUsesDarkApps(),
        };
        var palette = dark ? Palette.Dark : Palette.Light;
        if (MicaOn)
        {
            palette = OverBackdrop(palette, MicaBackdrop.Average(dark));
        }

        if (force || palette != Current)
        {
            Current = palette;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static ThemeRole RoleOf(Control control) =>
        Roles.TryGetValue(control, out var box) ? box.Value : ThemeRole.Default;

    private static void ApplyTree(Control control, Palette p)
    {
        ApplyOne(control, p);
        foreach (Control child in control.Controls)
        {
            ApplyTree(child, p);
        }

        if (control.ContextMenuStrip is { } menu)
        {
            ApplyMenu(menu, p);
        }
    }

    private static void ApplyOne(Control control, Palette p)
    {
        var role = RoleOf(control);
        var background = role == ThemeRole.Surface ? p.Surface : p.Background;
        switch (control)
        {
            case IThemed themed:
                themed.ApplyTheme(p);
                break;
            case TextBoxBase text:
                text.BackColor = p.Input;
                text.ForeColor = p.Text;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = p.Input;
                combo.ForeColor = p.Text;
                break;
            case ListBox list:
                list.BackColor = p.Input;
                list.ForeColor = p.Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
            case LinkLabel link:
                link.BackColor = Color.Transparent;
                link.LinkColor = p.Accent;
                link.ActiveLinkColor = p.Accent;
                link.VisitedLinkColor = p.Accent;
                link.ForeColor = p.Text;
                break;
            case CheckBox or RadioButton:
                control.BackColor = Color.Transparent;
                control.ForeColor = role == ThemeRole.Secondary ? p.SecondaryText : p.Text;
                ((ButtonBase)control).FlatStyle = FlatStyle.Standard;
                break;
            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = role == ThemeRole.Secondary ? p.SecondaryText : p.Text;
                break;
            case TreeView tree:
                tree.BackColor = p.Background;
                tree.ForeColor = p.Text;
                tree.LineColor = p.SecondaryText;
                if (tree.IsHandleCreated)
                {
                    NativeUi.SetExplorerTheme(tree.Handle, p.IsDark);
                }

                break;
            case ToolStrip strip:
                ApplyMenu(strip, p);
                break;
            case Panel when MicaOn && control is not Form:
                // Containers are see-through over the backdrop: on the window body it shows, on a card the card.
                control.BackColor = Color.Transparent;
                control.ForeColor = p.Text;
                break;
            default:
                control.BackColor = background;
                control.ForeColor = p.Text;
                break;
        }
    }

    public static void ApplyMenu(ToolStrip strip, Palette p)
    {
        ArgumentNullException.ThrowIfNull(strip);
        strip.Renderer = new ThemedMenuRenderer(p);
        strip.BackColor = p.Surface;
        strip.ForeColor = p.Text;
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = p.Text;
            if (item is ToolStripMenuItem { HasDropDownItems: true } menuItem)
            {
                ApplyMenu(menuItem.DropDown, p);
            }
        }
    }
}

/// <summary>
/// Fonts of plan §11.4, created once: the Windows 11 type ramp (body 14 px, title 20 px) in Segoe UI Variable,
/// or Segoe UI where it is missing (Windows 10). "Bold" is semibold, as Windows 11 uses it.
/// </summary>
internal static class UiFonts
{
    private static readonly HashSet<string> Installed = InstalledFamilies();

    public static Font Normal { get; } = new(Pick("Segoe UI Variable Text", "Segoe UI"), 10.5F);

    public static Font Bold { get; } = new(Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold"), 10.5F);

    public static Font Heading { get; } = new(Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold"), 15F);

    public static Font Strikeout { get; } = new(Normal.FontFamily, 10.5F, FontStyle.Strikeout);

    /// <summary>The comparison window's folder and key tree (15 px), a step larger than body text.</summary>
    public static Font Tree { get; } = new(Normal.FontFamily, 11.25F);

    /// <summary>Captions: secondary text, counts, the status bar (12 px).</summary>
    public static Font Caption { get; } = new(Pick("Segoe UI Variable Small", "Segoe UI"), 9F);

    /// <summary>Semibold caption: OLDER / NEWER tags, change counts.</summary>
    public static Font CaptionBold { get; } = new(Pick("Segoe UI Variable Small Semibol", "Segoe UI Semibold"), 9F);

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10: same code points.</summary>
    public static string GlyphFamily { get; } = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    public static Font Glyph(float size) => new(GlyphFamily, size);

    /// <summary>
    /// Draws an icon from the glyph font with grey edge smoothing, as Windows 11 draws icons. ClearType's colour
    /// smoothing thins the one-pixel upright strokes of outline icons (a folder lost its sides on some screens).
    /// </summary>
    public static void DrawGlyph(Graphics g, string glyph, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
    {
        ArgumentNullException.ThrowIfNull(g);
        var hint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        try
        {
            TextRenderer.DrawText(g, glyph, font, bounds, color, flags);
        }
        finally
        {
            g.TextRenderingHint = hint;
        }
    }

    /// <summary>The same face as <paramref name="font"/> in semibold (or bold where there is no semibold).</summary>
    public static Font SemiboldOf(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var family = font.FontFamily.Name switch
        {
            "Segoe UI Variable Text" => "Segoe UI Variable Text Semibold",
            "Segoe UI" => "Segoe UI Semibold",
            _ => null,
        };
        return family is not null && Installed.Contains(family) ? new Font(family, font.Size) : new Font(font, FontStyle.Bold);
    }

    private static string Pick(string preferred, string fallback) => Installed.Contains(preferred) ? preferred : fallback;

    private static HashSet<string> InstalledFamilies()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        return installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Code points shared by Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
internal static class Glyphs
{
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string Settings = "\uE713";
    public const string Help = "\uE897";
    public const string Exit = "\uE7E8";
    public const string Swap = "\uE8AB";
    public const string Refresh = "\uE72C";
    public const string Rename = "\uE8AC";
    public const string Info = "\uE946";
    public const string Compare = "\uE8A9";
    public const string Folder = "\uE8B7";
    public const string Stop = "\uE71A";
    public const string Play = "\uE768";
    public const string Shield = "\uEA18";
    public const string File = "\uE7C3";
    public const string Drive = "\uEDA2";
    public const string Value = "\uE8EC";

    /// <summary>An environment variable (Segoe Fluent "Code").</summary>
    public const string Variable = "\uE943";
}
