using System.Drawing.Drawing2D;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A Windows 11 layer: a rounded card in the background colour with a thin border, on the window's Mica tint
/// (<see cref="Palette.Surface"/>). Over the wallpaper backdrop it is a translucent white with the backdrop showing
/// through, as the cards in Settings. Children go inside <see cref="Control.Padding"/>, clear of the corners.
/// </summary>
internal class LayerPanel : Panel, IThemed
{
    private Palette _palette = ThemeManager.Current;

    public LayerPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Padding = new Padding(4);
    }

    /// <summary>A plain card shows the backdrop through it; a tinted one (an info bar) stays solid.</summary>
    private bool OverBackdrop => ThemeManager.MicaOn && GetType() == typeof(LayerPanel);

    /// <summary>Corner radius at 96 DPI.</summary>
    public int Radius { get; set; } = 8;

    public virtual void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = OverBackdrop ? Color.Transparent : palette.Surface; // see-through: its children show the card
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint draws everything, what is behind the card included.
    }

    protected virtual Color Fill(Palette palette) => palette.Background;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ThemeManager.ClearBehind(g, this, _palette.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ThemedButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(Radius));
        using var brush = new SolidBrush(OverBackdrop ? ThemeManager.LayerOverBackdrop(_palette) : Fill(_palette));
        using var pen = new Pen(_palette.Border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>A plain container in the layer colour: the area inside a <see cref="LayerPanel"/>.</summary>
internal sealed class LayerFill : Panel, IThemed
{
    public Func<Palette, Color> Color { get; set; } = p => p.Background;

    public void ApplyTheme(Palette palette) => BackColor = Color(palette);
}

/// <summary>
/// A Windows 11 info bar in the "caution" style: icon, bold title, message, an optional action button and a close
/// button, on a rounded tinted card.
/// </summary>
internal sealed class InfoBar : LayerPanel
{
    private readonly LayerFill _content = new() { Dock = DockStyle.Fill, Color = p => p.CautionBackground };
    private readonly CautionIcon _icon = new() { Dock = DockStyle.Left, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Text = "\uE7BA" };
    private readonly Label _title = new() { Dock = DockStyle.Left, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Font = UiFonts.Bold, Padding = new Padding(0, 0, 8, 0) };
    private readonly Label _message = new() { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0) };
    private readonly ThemedButton _close = new() { Glyph = "\uE711", Text = string.Empty, Subtle = true, AccessibleName = "Close", MinimumSize = new Size(32, 32), Size = new Size(32, 32) };

    public InfoBar()
    {
        Radius = 4;
        Padding = new Padding(10, 1, 4, 1);
        _icon.Font = UiFonts.Glyph(12F);
        _icon.Width = 28;
        _close.Margin = new Padding(4, 0, 0, 0);
        _close.Click += (_, _) =>
        {
            Visible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        };
        _buttons.Controls.Add(_close);
        _content.Controls.Add(_message);
        _content.Controls.Add(_title);
        _content.Controls.Add(_icon);
        _content.Controls.Add(_buttons);
        Controls.Add(_content);
        _content.Resize += (_, _) => CenterButtons();
        _buttons.SizeChanged += (_, _) => CenterButtons();
    }

    /// <summary>Raised when the close button hides the bar.</summary>
    public event EventHandler? Closed;

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string Message
    {
        get => _message.Text;
        set => _message.Text = value;
    }

    /// <summary>Puts a button before the close button.</summary>
    public void AddAction(ThemedButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.Margin = new Padding(8, 0, 0, 0);
        button.Width = button.PreferredButtonWidth();
        _buttons.Controls.Add(button);
        _buttons.Controls.SetChildIndex(button, 0);
    }

    protected override Color Fill(Palette palette) => palette.CautionBackground;

    private void CenterButtons()
    {
        var top = Math.Max(0, (_content.ClientSize.Height - _close.Height) / 2);
        _buttons.Padding = new Padding(0, top, 0, 0);
    }

    /// <summary>The warning glyph in the caution colour (a plain label would be recoloured to normal text).</summary>
    private sealed class CautionIcon : Label, IThemed
    {
        public void ApplyTheme(Palette palette)
        {
            BackColor = Color.Transparent;
            ForeColor = palette.Caution;
        }
    }
}

/// <summary>
/// Windows 11 selector bar: a row of text tabs, the chosen one semibold with a short accent line below it.
/// Left / Right arrows move between them.
/// </summary>
internal sealed class SelectorBar : Control, IThemed
{
    private readonly List<string> _items = [];
    private Palette _palette = ThemeManager.Current;
    private int _selected;
    private int _hover = -1;

    public SelectorBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Font = UiFonts.Normal;
        Height = 40;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public event EventHandler? SelectedIndexChanged;

    public IList<string> Items => _items;

    /// <summary>Renames a tab (a count in it changed).</summary>
    public void SetItem(int index, string text)
    {
        _items[index] = text;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, _items.Count - 1));
            if (value == _selected)
            {
                return;
            }

            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left)
        {
            SelectedIndex--;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right)
        {
            SelectedIndex++;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.X);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var hit = HitTest(e.X);
        if (e.Button == MouseButtons.Left && hit >= 0)
        {
            SelectedIndex = hit;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = _palette;
        var g = e.Graphics;
        ThemeManager.ClearBehind(g, this, p.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ItemBounds();
        for (var i = 0; i < _items.Count; i++)
        {
            var box = bounds[i];
            var chosen = i == _selected;
            if (i == _hover && !chosen)
            {
                using var path = ThemedButton.Rounded(Rectangle.Inflate(box, 0, -4), 4);
                using var brush = new SolidBrush(p.Hover);
                g.FillPath(brush, path);
            }

            var font = chosen ? UiFonts.Bold : Font;
            TextRenderer.DrawText(g, _items[i], font, box, chosen || i == _hover ? p.Text : p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (chosen)
            {
                var width = LogicalToDeviceUnits(16);
                var height = LogicalToDeviceUnits(3);
                using var pill = ThemedButton.Rounded(new Rectangle(box.Left + ((box.Width - width) / 2), box.Bottom - height - LogicalToDeviceUnits(2), width, height), 1);
                using var accent = new SolidBrush(p.Accent);
                g.FillPath(accent, pill);
            }

            if (chosen && Focused && ShowFocusCues)
            {
                using var path = ThemedButton.Rounded(Rectangle.Inflate(box, 0, -4), 4);
                using var pen = new Pen(p.Text, 1.5f);
                g.DrawPath(pen, path);
            }
        }
    }

    private int HitTest(int x)
    {
        var bounds = ItemBounds();
        for (var i = 0; i < bounds.Count; i++)
        {
            if (x >= bounds[i].Left && x < bounds[i].Right)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Each item as wide as its semibold text plus 12 px at each side, 4 px apart.</summary>
    private List<Rectangle> ItemBounds()
    {
        var result = new List<Rectangle>(_items.Count);
        var x = 0;
        foreach (var item in _items)
        {
            var width = TextRenderer.MeasureText(item, UiFonts.Bold, Size.Empty, TextFormatFlags.NoPadding).Width + LogicalToDeviceUnits(24);
            result.Add(new Rectangle(x, 0, width, Height));
            x += width + LogicalToDeviceUnits(4);
        }

        return result;
    }
}

/// <summary>
/// The folder or key shown in the list, as a path bar: "C:\ › Program Files › Contoso", the last part semibold.
/// Parts that do not fit are dropped from the start ("… › Contoso"). Display only: the parts are not links.
/// </summary>
internal sealed class PathBar : Control, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private readonly Font _chevron = UiFonts.Glyph(7F);

    public PathBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = UiFonts.Normal;
        TabStop = false;
    }

    /// <summary>Width the whole path needs.</summary>
    public int PreferredPathWidth => Measure(Parts(Text)).Sum() + 2;

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = _palette;
        var g = e.Graphics;
        ThemeManager.ClearBehind(g, this, p.Background);
        var parts = Parts(Text);
        if (parts.Count == 0)
        {
            return;
        }

        // Drop leading parts until the rest fits, then show "…" first.
        var widths = Measure(parts);
        var first = 0;
        var ellipsis = TextRenderer.MeasureText("…", Font, Size.Empty, TextFormatFlags.NoPadding).Width + SeparatorWidth;
        while (first < parts.Count - 1 && widths.Skip(first).Sum() + (first > 0 ? ellipsis : 0) > Width)
        {
            first++;
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var x = 0;
        if (first > 0)
        {
            x = DrawPart(g, "…", Font, p.SecondaryText, x, last: false, flags);
        }

        for (var i = first; i < parts.Count; i++)
        {
            var last = i == parts.Count - 1;
            x = DrawPart(g, parts[i], last ? UiFonts.Bold : Font, last ? p.Text : p.SecondaryText, x, last, flags | (last ? TextFormatFlags.EndEllipsis : 0));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chevron.Dispose();
        }

        base.Dispose(disposing);
    }

    private int SeparatorWidth => LogicalToDeviceUnits(22);

    private int DrawPart(Graphics g, string text, Font font, Color color, int x, bool last, TextFormatFlags flags)
    {
        var width = last ? Width - x : TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, text, font, new Rectangle(x, 0, width, Height), color, flags);
        if (last)
        {
            return Width;
        }

        x += width;
        TextRenderer.DrawText(g, "\uE76C", _chevron, new Rectangle(x, 0, SeparatorWidth, Height), _palette.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return x + SeparatorWidth;
    }

    private List<int> Measure(List<string> parts) =>
        [.. parts.Select((part, i) => TextRenderer.MeasureText(part, i == parts.Count - 1 ? UiFonts.Bold : Font, Size.Empty, TextFormatFlags.NoPadding).Width + (i < parts.Count - 1 ? SeparatorWidth : 0))];

    /// <summary>"C:\Program Files\Contoso" → "C:\", "Program Files", "Contoso"; a drive keeps its backslash.</summary>
    private static List<string> Parts(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].EndsWith(':'))
        {
            parts[0] += "\\";
        }

        return parts;
    }
}

/// <summary>A small caption on a subtle rounded fill: OLDER / NEWER before a snapshot name (issue #6).</summary>
internal sealed class TagLabel : Control, IThemed
{
    private Palette _palette = ThemeManager.Current;

    public TagLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = UiFonts.CaptionBold;
        TabStop = false;
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(text.Width + LogicalToDeviceUnits(12), text.Height + LogicalToDeviceUnits(4));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        ThemeManager.ClearBehind(g, this, _palette.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = ThemedButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 4))
        using (var brush = new SolidBrush(_palette.IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0)))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _palette.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}
