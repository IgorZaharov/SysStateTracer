using System.Drawing.Drawing2D;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A Windows 11 button in the palette: the primary one in the accent colour, standard ones on a light control fill
/// with a darker bottom edge, and subtle ones (command bars) with no fill until the mouse is over them.
/// </summary>
internal sealed class ThemedButton : Button, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private Font? _glyphFont;
    private bool _hover;
    private bool _pressed;

    public ThemedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Font = UiFonts.Normal;
        Height = 32;
        MinimumSize = new Size(88, 32);
        AutoSize = false;
        Cursor = Cursors.Default;
    }

    /// <summary>The focused button is always outlined, also before the keyboard was used, so Enter's target is visible.</summary>
    protected override bool ShowFocusCues => true;

    /// <summary>Accent-coloured: the main action of the window.</summary>
    public bool Primary { get; set; }

    /// <summary>No fill or border until the mouse is over it (command bars).</summary>
    public bool Subtle { get; set; }

    /// <summary>Optional icon drawn before the text; with no text the icon is drawn alone.</summary>
    public string? Glyph { get; set; }

    private Font GlyphFont => _glyphFont ??= UiFonts.Glyph(Font.Size);

    /// <summary>Width that fits the icon and the text with Windows 11 padding (11 px at each side, 8 px between).</summary>
    public int PreferredButtonWidth()
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        if (Glyph is null)
        {
            return text + LogicalToDeviceUnits(24);
        }

        var glyph = TextRenderer.MeasureText(Glyph, GlyphFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        return Text.Length == 0 ? Height : glyph + text + LogicalToDeviceUnits(32);
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = mevent.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _glyphFont?.Dispose();
        _glyphFont = null;
        base.OnFontChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var p = _palette;
        var g = pevent.Graphics;
        var behind = Parent?.BackColor ?? p.Background;
        g.Clear(behind);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Windows 11 control fills and strokes, as translucent layers over what is behind the button.
        var control = p.IsDark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(179, 255, 255, 255);
        Color? fill;
        Color? border = null;
        Color? bottom = null;
        Color text;
        if (!Enabled)
        {
            fill = Primary ? (p.IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(56, 0, 0, 0)) : Subtle ? null : control;
            border = Primary || Subtle ? null : Stroke(p, bottom: false);
            text = Primary ? (p.IsDark ? Color.FromArgb(135, 255, 255, 255) : Color.White) : Palette.Blend(p.SecondaryText, behind, 0.45);
        }
        else if (Primary)
        {
            fill = _pressed ? Color.FromArgb(204, p.Accent) : _hover ? Color.FromArgb(230, p.Accent) : p.Accent;
            bottom = _pressed ? null : Color.FromArgb(p.IsDark ? 36 : 102, 0, 0, 0);
            text = _pressed ? Palette.Blend(p.AccentText, p.Accent, 0.3) : p.AccentText;
        }
        else if (Subtle)
        {
            fill = _pressed ? (p.IsDark ? Color.FromArgb(11, 255, 255, 255) : Color.FromArgb(6, 0, 0, 0))
                : _hover ? (p.IsDark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0))
                : null;
            text = _pressed ? p.SecondaryText : p.Text;
        }
        else
        {
            fill = _pressed ? (p.IsDark ? Color.FromArgb(8, 255, 255, 255) : Color.FromArgb(77, 249, 249, 249))
                : _hover ? (p.IsDark ? Color.FromArgb(21, 255, 255, 255) : Color.FromArgb(128, 249, 249, 249))
                : control;
            border = Stroke(p, bottom: false);
            bottom = _pressed ? null : Stroke(p, bottom: true);
            text = _pressed ? p.SecondaryText : p.Text;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, 4))
        {
            if (fill is { } f)
            {
                using var brush = new SolidBrush(f);
                g.FillPath(brush, path);
            }

            if (border is { } b)
            {
                using var pen = new Pen(b);
                g.DrawPath(pen, path);
            }
        }

        if (bottom is { } edge)
        {
            // The darker bottom edge that gives Windows 11 buttons their depth.
            using var pen = new Pen(edge);
            g.SmoothingMode = SmoothingMode.None;
            g.DrawLine(pen, 3, Height - 1, Width - 4, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        if (Focused && ShowFocusCues && Enabled)
        {
            using var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 3);
            using var pen = new Pen(p.Text, 1.5f);
            g.DrawPath(pen, path);
        }

        if (Glyph is null)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }

        if (Text.Length == 0)
        {
            TextRenderer.DrawText(g, Glyph, GlyphFont, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var glyphSize = TextRenderer.MeasureText(g, Glyph, GlyphFont, Size.Empty, TextFormatFlags.NoPadding);
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var gap = LogicalToDeviceUnits(8);
        var x = Math.Max(LogicalToDeviceUnits(8), (Width - (glyphSize.Width + gap + textSize.Width)) / 2);
        TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(x, 0, glyphSize.Width, Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var textLeft = x + glyphSize.Width + gap;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft - 4, Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glyphFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Control border: the top and sides, or the darker bottom edge.</summary>
    private static Color Stroke(Palette p, bool bottom) => p.IsDark
        ? Color.FromArgb(bottom ? 18 : 24, 255, 255, 255)
        : Color.FromArgb(bottom ? 41 : 15, 0, 0, 0);

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
