using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A sidebar icon (plan Step 42): an icon-font glyph with a tooltip; hover and press change its
/// background and tint the glyph with the accent colour; disabled draws it dimmed.
/// </summary>
internal sealed class SideBarButton : Control, IThemed
{
    private readonly Font _glyphFont = UiFonts.Glyph(15F);
    private Palette _palette = ThemeManager.Current;
    private bool _hover;
    private bool _pressed;

    public SideBarButton(string glyph, string caption)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Glyph = glyph;
        Text = caption;
        AccessibleName = caption;
        AccessibleRole = AccessibleRole.PushButton;
        Size = new Size(40, 40);
        Margin = new Padding(4, 4, 4, 0);
        TabStop = true;
    }

    public string Glyph { get; }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var click = _pressed && ClientRectangle.Contains(e.Location);
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        if (click)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space && Enabled)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = _palette;
        var g = e.Graphics;
        g.Clear(p.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (Enabled && (_hover || _pressed))
        {
            using var path = ThemedButton.Rounded(new Rectangle(2, 2, Width - 5, Height - 5), 4);
            using var brush = new SolidBrush(_pressed ? p.Pressed : p.Hover);
            g.FillPath(brush, path);
        }

        if (Focused && ShowFocusCues)
        {
            using var path = ThemedButton.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 4);
            using var pen = new Pen(p.Accent);
            g.DrawPath(pen, path);
        }

        var color = !Enabled ? Palette.Blend(p.SecondaryText, p.Surface, 0.45) : _hover || _pressed ? p.Accent : p.Text;
        TextRenderer.DrawText(g, Glyph, _glyphFont, ClientRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glyphFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
