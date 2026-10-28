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

    public static event EventHandler? Changed;

    public static ThemePreference Preference => _preference;

    public static Palette Current { get; private set; } = Palette.Light;

    /// <summary>Sets the preference (from Preferences) and recolours open windows when the result changes.</summary>
    public static void SetPreference(ThemePreference preference)
    {
        _preference = preference;
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
        }

        Update();
    }

    /// <summary>Forces a palette (screenshots and tests).</summary>
    public static void SetPalette(Palette palette)
    {
        Current = palette;
        Changed?.Invoke(null, EventArgs.Empty);
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
        void OnChanged(object? sender, EventArgs e)
        {
            if (form.IsDisposed)
            {
                return;
            }

            if (form.InvokeRequired)
            {
                form.BeginInvoke(() => Apply(form));
            }
            else
            {
                Apply(form);
            }
        }

        Changed += OnChanged;
        form.FormClosed += (_, _) => Changed -= OnChanged;
        form.HandleCreated += (_, _) => NativeUi.SetDarkTitleBar(form.Handle, Current.IsDark);
        Apply(form);
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

    private static void Update()
    {
        var dark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => WindowsUsesDarkApps(),
        };
        var palette = dark ? Palette.Dark : Palette.Light;
        if (!ReferenceEquals(palette, Current))
        {
            SetPalette(palette);
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

/// <summary>Fonts of plan §11.4, created once.</summary>
internal static class UiFonts
{
    public static Font Normal { get; } = new("Segoe UI", 9F);

    public static Font Bold { get; } = new("Segoe UI", 9F, FontStyle.Bold);

    public static Font Heading { get; } = new("Segoe UI Semibold", 11F);

    public static Font Strikeout { get; } = new("Segoe UI", 9F, FontStyle.Strikeout);

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10: same code points.</summary>
    public static string GlyphFamily { get; } = PickGlyphFamily();

    public static Font Glyph(float size) => new(GlyphFamily, size);

    private static string PickGlyphFamily()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        return installed.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
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
}
