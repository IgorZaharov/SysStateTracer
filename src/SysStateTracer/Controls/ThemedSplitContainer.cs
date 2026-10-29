using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>A split container in the palette: panes on the background, a thin line in the border colour as the splitter.</summary>
internal sealed class ThemedSplitContainer : SplitContainer, IThemed
{
    private Palette _palette = ThemeManager.Current;

    public ThemedSplitContainer()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SplitterWidth = 5;
        TabStop = false;
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        Panel1.BackColor = palette.Background;
        Panel2.BackColor = palette.Background;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = SplitterRectangle;
        using (var background = new SolidBrush(_palette.Background))
        {
            e.Graphics.FillRectangle(background, r);
        }

        using var line = new Pen(_palette.Border);
        if (Orientation == Orientation.Vertical)
        {
            var x = r.Left + (r.Width / 2);
            e.Graphics.DrawLine(line, x, r.Top, x, r.Bottom);
        }
        else
        {
            var y = r.Top + (r.Height / 2);
            e.Graphics.DrawLine(line, r.Left, y, r.Right, y);
        }
    }
}
