namespace SysStateTracer.Theming;

/// <summary>
/// The colour tokens of plan §11.4, swapped as a whole when the theme changes. Values follow the
/// Windows 11 defaults; green, yellow and red are used for change state only.
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
        Background: Hex(0xFFFFFF),
        Surface: Hex(0xF3F3F3),
        Text: Hex(0x1B1B1B),
        SecondaryText: Hex(0x616161),
        Accent: Hex(0x005FB8),
        AccentText: Hex(0xFFFFFF),
        New: Hex(0x0F7B0F),
        Modified: Hex(0x9D5D00),
        Deleted: Hex(0xC42B1C),
        Border: Hex(0xE0E0E0),
        Hover: Hex(0xE8E8E8),
        Pressed: Hex(0xDADADA),
        Selection: Hex(0xCCE4F7),
        SelectionInactive: Hex(0xE5E5E5),
        Input: Hex(0xFFFFFF),
        DropTarget: Hex(0xB9D7F2));

    public static Palette Dark { get; } = new(
        IsDark: true,
        Background: Hex(0x202020),
        Surface: Hex(0x2B2B2B),
        Text: Hex(0xF3F3F3),
        SecondaryText: Hex(0xA0A0A0),
        Accent: Hex(0x60CDFF),
        AccentText: Hex(0x000000),
        New: Hex(0x6CCB5F),
        Modified: Hex(0xFCE100),
        Deleted: Hex(0xFF99A4),
        Border: Hex(0x3C3C3C),
        Hover: Hex(0x383838),
        Pressed: Hex(0x323232),
        Selection: Hex(0x1E4363),
        SelectionInactive: Hex(0x3A3A3A),
        Input: Hex(0x2D2D2D),
        DropTarget: Hex(0x245A85));

    /// <summary>A colour between <paramref name="a"/> (0) and <paramref name="b"/> (1).</summary>
    public static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
        (int)Math.Round(a.R + ((b.R - a.R) * amount)),
        (int)Math.Round(a.G + ((b.G - a.G) * amount)),
        (int)Math.Round(a.B + ((b.B - a.B) * amount)));

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
