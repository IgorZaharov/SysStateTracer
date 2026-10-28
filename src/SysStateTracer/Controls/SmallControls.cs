using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>Tooltips drawn in the current palette (surface colour, as plan §11.4 says).</summary>
internal sealed class ThemedToolTip : ToolTip
{
    public ThemedToolTip()
    {
        OwnerDraw = true;
        InitialDelay = 400;
        ReshowDelay = 100;
        AutoPopDelay = 15000;
        Popup += (_, e) =>
        {
            var text = GetToolTip(e.AssociatedControl!);
            var size = TextRenderer.MeasureText(text, UiFonts.Normal, new Size(480, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(size.Width + 12, size.Height + 8);
        };
        Draw += (_, e) =>
        {
            var p = ThemeManager.Current;
            using (var brush = new SolidBrush(p.Surface))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }

            using (var pen = new Pen(p.Border))
            {
                e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1));
            }

            var textBounds = Rectangle.Inflate(e.Bounds, -6, -4);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, UiFonts.Normal, textBounds, p.Text, TextFormatFlags.WordBreak | TextFormatFlags.Left);
        };
    }
}

/// <summary>A flat progress bar in the accent colour; <see cref="Marquee"/> when there is no estimate.</summary>
internal sealed class ThemedProgressBar : Control, IThemed
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private Palette _palette = ThemeManager.Current;
    private double _value;
    private bool _marquee;
    private int _marqueeOffset;

    public ThemedProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 4;
        TabStop = false;
        _timer.Tick += (_, _) =>
        {
            _marqueeOffset = (_marqueeOffset + 6) % Math.Max(1, Width + (Width / 4));
            Invalidate();
        };
    }

    /// <summary>0–1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    /// <summary>A moving block instead of a fill (no estimate yet).</summary>
    public bool Marquee
    {
        get => _marquee;
        set
        {
            _marquee = value;
            _timer.Enabled = value && Visible;
            Invalidate();
        }
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        _timer.Enabled = _marquee && Visible;
        base.OnVisibleChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = _palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Background);
        var track = new Rectangle(0, (Height - 4) / 2, Width, 4);
        using (var brush = new SolidBrush(p.Border))
        {
            g.FillRectangle(brush, track);
        }

        using var fill = new SolidBrush(p.Accent);
        if (_marquee)
        {
            var block = Math.Max(20, Width / 4);
            g.FillRectangle(fill, new Rectangle(_marqueeOffset - block, track.Y, block, track.Height));
        }
        else
        {
            g.FillRectangle(fill, new Rectangle(0, track.Y, (int)(Width * _value), track.Height));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A thin line between areas.</summary>
internal sealed class Separator : Control, IThemed
{
    public Separator()
    {
        Height = 1;
        TabStop = false;
    }

    public void ApplyTheme(Palette palette) => BackColor = palette.Border;
}
