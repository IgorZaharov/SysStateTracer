using System.Drawing.Drawing2D;
using SysStateTracer.Core.Config;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A small picture of the main window in a theme, shown above each theme choice in Preferences (F5).
/// "Follow Windows" shows the light and the dark window side by side.
/// </summary>
internal sealed class ThemePreview : Control, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private bool _selected;
    private bool _hover;

    public ThemePreview(ThemePreference theme)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme = theme;
        Size = new Size(132, 84);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = theme switch
        {
            ThemePreference.Light => "Light theme",
            ThemePreference.Dark => "Dark theme",
            _ => "Follow the Windows theme",
        };
    }

    public ThemePreference Theme { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    /// <summary>Clicks the picture (for tests).</summary>
    internal void PerformClick() => OnClick(EventArgs.Empty);

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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _palette.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var frame = new Rectangle(3, 3, Width - 7, Height - 7);

        using (var clip = ThemedButton.Rounded(frame, 6))
        {
            var state = g.Save();
            g.SetClip(clip);
            if (Theme == ThemePreference.System)
            {
                DrawWindow(g, frame, Palette.Light);
                var half = new Rectangle(frame.X + (frame.Width / 2), frame.Y, frame.Width - (frame.Width / 2), frame.Height);
                g.SetClip(half, CombineMode.Intersect);
                DrawWindow(g, frame, Palette.Dark);
            }
            else
            {
                DrawWindow(g, frame, Theme == ThemePreference.Dark ? Palette.Dark : Palette.Light);
            }

            g.Restore(state);
        }

        var border = _selected ? _palette.Accent : _hover ? _palette.SecondaryText : _palette.Border;
        using var pen = new Pen(border, _selected ? 2.5f : 1f);
        using var outline = ThemedButton.Rounded(frame, 6);
        g.DrawPath(pen, outline);
    }

    /// <summary>A miniature main window: title bar, sidebar, header, rows in the change colours.</summary>
    private static void DrawWindow(Graphics g, Rectangle r, Palette p)
    {
        using var background = new SolidBrush(p.Background);
        using var surface = new SolidBrush(p.Surface);
        using var text = new SolidBrush(p.Text);
        using var secondary = new SolidBrush(p.SecondaryText);
        using var accent = new SolidBrush(p.Accent);
        using var selection = new SolidBrush(p.Selection);
        g.FillRectangle(background, r);

        // Title bar and sidebar.
        g.FillRectangle(surface, r.X, r.Y, r.Width, 12);
        g.FillRectangle(surface, r.X, r.Y + 12, 16, r.Height - 12);
        for (var i = 0; i < 3; i++)
        {
            g.FillRectangle(secondary, r.X + 5, r.Y + 18 + (i * 10), 6, 5);
        }

        // Header and rows: new, modified, deleted, plain, selected.
        var x = r.X + 22;
        var width = r.Width - 28;
        g.FillRectangle(text, x, r.Y + 17, width / 3, 4);
        Color[] rows = [p.New, p.Modified, p.Deleted, p.Text];
        for (var i = 0; i < rows.Length; i++)
        {
            var y = r.Y + 27 + (i * 9);
            if (i == 1)
            {
                g.FillRectangle(selection, x - 2, y - 2, width + 2, 8);
            }

            using var row = new SolidBrush(rows[i]);
            g.FillRectangle(row, x, y, width / 2, 3);
            g.FillRectangle(secondary, x + (width / 2) + 6, y, width / 3, 3);
        }

        // The main button.
        g.FillRectangle(accent, r.Right - 26, r.Bottom - 12, 20, 7);
    }
}
