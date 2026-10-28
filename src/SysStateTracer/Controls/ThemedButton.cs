using System.Drawing.Drawing2D;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>A flat button in the palette: the primary one in the accent colour, others on the surface colour.</summary>
internal sealed class ThemedButton : Button, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private bool _hover;
    private bool _pressed;

    public ThemedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Font = UiFonts.Normal;
        Height = 30;
        MinimumSize = new Size(88, 30);
        AutoSize = false;
        Cursor = Cursors.Default;
    }

    /// <summary>The focused button is always outlined, also before the keyboard was used, so Enter's target is visible.</summary>
    protected override bool ShowFocusCues => true;

    /// <summary>Accent-coloured: the main action of the window.</summary>
    public bool Primary { get; set; }

    /// <summary>Optional icon drawn before the text.</summary>
    public string? Glyph { get; set; }

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

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var p = _palette;
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, border, text;
        if (!Enabled)
        {
            fill = Primary ? Palette.Blend(p.Accent, p.Background, 0.6) : p.Surface;
            border = Primary ? fill : p.Border;
            text = Primary ? Palette.Blend(p.AccentText, fill, 0.4) : Palette.Blend(p.SecondaryText, p.Surface, 0.3);
        }
        else if (Primary)
        {
            fill = _pressed ? Palette.Blend(p.Accent, p.Background, 0.25) : _hover ? Palette.Blend(p.Accent, p.Background, 0.12) : p.Accent;
            border = fill;
            text = p.AccentText;
        }
        else
        {
            fill = _pressed ? p.Pressed : _hover ? p.Hover : p.IsDark ? p.Input : p.Background;
            border = p.Border;
            text = p.Text;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, 4))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(Focused && ShowFocusCues && Enabled ? p.Accent : border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (Glyph is null)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, flags);
            return;
        }

        using var glyphFont = UiFonts.Glyph(10F);
        var textSize = TextRenderer.MeasureText(g, Text, Font);
        var glyphSize = TextRenderer.MeasureText(g, Glyph, glyphFont, Size.Empty, TextFormatFlags.NoPadding);
        var total = glyphSize.Width + 6 + textSize.Width;
        var x = (Width - total) / 2;
        TextRenderer.DrawText(g, Glyph, glyphFont, new Rectangle(x, 0, glyphSize.Width, Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphSize.Width + 6, 0, textSize.Width, Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

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
