using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>What the comparison window's colours mean, in one line: new, modified, deleted, changes inside, unchanged, changed value.</summary>
internal sealed class ColorLegend : Control, IThemed
{
    private const int Gap = 16;
    private const int IconWidth = 16;
    private readonly Font _iconFont = UiFonts.Glyph(10F);
    private Palette _palette = ThemeManager.Current;

    public ColorLegend()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = UiFonts.Normal;
        TabStop = false;
        Width = PreferredWidth();
    }

    private static readonly (string Sign, string Label, Func<Palette, Color> Color, bool Chip)[] Entries =
    [
        ("+", "New", p => p.New, false),
        ("~", "Modified", p => p.Modified, false),
        ("−", "Deleted", p => p.Deleted, false),
        (string.Empty, "Changes inside", p => p.Inside, false),
        (string.Empty, "Unchanged", p => p.Text, false),
        (string.Empty, "Changed value", p => p.SecondaryText, true),
    ];

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        Invalidate();
    }

    private int EntryWidth((string Sign, string Label, Func<Palette, Color> Color, bool Chip) entry) =>
        (entry.Chip ? 22 : IconWidth) + 4 + (entry.Sign.Length > 0 ? 12 : 0) + TextRenderer.MeasureText(entry.Label, Font).Width;

    private int PreferredWidth() => Entries.Sum(EntryWidth) + (Gap * (Entries.Length - 1)) + 4;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Width = PreferredWidth();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _palette;
        g.Clear(p.Background);
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var x = 2;
        var height = Height;
        foreach (var entry in Entries)
        {
            var color = entry.Color(p);
            if (entry.Chip)
            {
                using var chip = new SolidBrush(Palette.Blend(p.Modified, p.Background, p.IsDark ? 0.78 : 0.84));
                g.FillRectangle(chip, x, (height / 2) - 6, 22, 12);
                x += 22 + 4;
            }
            else
            {
                var iconColor = entry.Label == "Unchanged" ? p.SecondaryText : color;
                TextRenderer.DrawText(g, Glyphs.Folder, _iconFont, new Rectangle(x, 0, IconWidth, height), iconColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += IconWidth + 4;
            }

            if (entry.Sign.Length > 0)
            {
                TextRenderer.DrawText(g, entry.Sign, UiFonts.Bold, new Rectangle(x, 0, 12, height), color, flags);
                x += 12;
            }

            var label = TextRenderer.MeasureText(entry.Label, Font).Width;
            TextRenderer.DrawText(g, entry.Label, Font, new Rectangle(x, 0, label + 2, height), entry.Chip ? p.SecondaryText : color, flags);
            x += label + Gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
