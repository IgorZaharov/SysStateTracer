namespace SysStateTracer.Theming;

/// <summary>
/// The colour tokens of plan §11.4, swapped as a whole when the theme changes. Values follow the
/// Windows 11 defaults; green, yellow and red are used for change state only.
/// <see cref="Surface"/> is Mica's tint (the window body around the content, the colour of the Mica title bar);
/// <see cref="Background"/> is the slightly lighter layer that lists, trees and dialogs sit on.
/// Selection is a subtle fill with an accent bar at the row's left end, as in Windows 11 lists.
/// </summary>
internal sealed record Palette(
    bool IsDark,
    Color Background,
    Color Surface,
    Color Text,
    Color SecondaryText,
    Color Accent,
    Color AccentText,
    Color New,
    Color Modified,
    Color Deleted,
    Color Border,
    Color Hover,
    Color Pressed,
    Color Selection,
    Color SelectionInactive,
    Color Input,
    Color DropTarget)
{
    public static Palette Light { get; } = new(
        IsDark: false,
        Background: Hex(0xF9F9F9),
        Surface: Hex(0xF3F3F3),
        Text: Hex(0x1B1B1B),
        SecondaryText: Hex(0x5F5F5F),
        Accent: Hex(0x005FB8),
        AccentText: Hex(0xFFFFFF),
        New: Hex(0x0F7B0F),
        Modified: Hex(0x9D5D00),
        Deleted: Hex(0xC42B1C),
        Border: Hex(0xE3E3E3),
        Hover: Hex(0xEAEAEA),
        Pressed: Hex(0xEFEFEF),
        Selection: Hex(0xD8E6F3),
        SelectionInactive: Hex(0xE4ECF4),
        Input: Hex(0xFFFFFF),
        DropTarget: Hex(0xB9D7F2));

    public static Palette Dark { get; } = new(
        IsDark: true,
        Background: Hex(0x282828),
        Surface: Hex(0x202020),
        Text: Hex(0xFFFFFF),
        SecondaryText: Hex(0xC8C8C8),
        Accent: Hex(0x60CDFF),
        AccentText: Hex(0x000000),
        New: Hex(0x6CCB5F),
        Modified: Hex(0xF7A44A), // orange, like the light theme (was yellow)
        Deleted: Hex(0xFF99A4),
        Border: Hex(0x3A3A3A),
        Hover: Hex(0x353535),
        Pressed: Hex(0x2F2F2F),
        Selection: Hex(0x2C3E4F),
        SelectionInactive: Hex(0x313A43),
        Input: Hex(0x2D2D2D),
        DropTarget: Hex(0x245A85));

    /// <summary>A folder or key that is the same itself but holds changes (blue in the comparison tree).</summary>
    public Color Inside => IsDark ? Hex(0x6CB8FF) : Hex(0x0060C0);

    /// <summary>Icon and title of a warning bar (Windows 11 "caution" info bar).</summary>
    public Color Caution => IsDark ? Hex(0xFCE100) : Hex(0x9D5D00);

    /// <summary>Background of a warning bar.</summary>
    public Color CautionBackground => IsDark ? Hex(0x433519) : Hex(0xFFF4CE);

    /// <summary>Background of every other row when zebra striping is on.</summary>
    public Color Stripe => Blend(Background, Text, IsDark ? 0.05 : 0.035);

    /// <summary>Grid lines between rows and columns.</summary>
    public Color GridLine => Blend(Background, Text, IsDark ? 0.14 : 0.11);

    /// <summary>A colour between <paramref name="a"/> (0) and <paramref name="b"/> (1).</summary>
    public static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
        (int)Math.Round(a.R + ((b.R - a.R) * amount)),
        (int)Math.Round(a.G + ((b.G - a.G) * amount)),
        (int)Math.Round(a.B + ((b.B - a.B) * amount)));

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
