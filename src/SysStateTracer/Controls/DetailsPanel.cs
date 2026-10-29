using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// The comparison window's details: the selected item's name, change and path, then its properties in the left
/// and right snapshot side by side; values that differ are highlighted.
/// </summary>
internal sealed class DetailsPanel : Control, IThemed
{
    private const int LabelWidth = 130;
    private const int LineHeight = 22;
    private Palette _palette = ThemeManager.Current;
    private string _title = string.Empty;
    private string _status = string.Empty;
    private Color? _statusColor;
    private string _path = string.Empty;
    private string _leftHeader = string.Empty;
    private string _rightHeader = string.Empty;
    private IReadOnlyList<ComparisonData.DetailRow> _rows = [];
    private string _message = "Select a folder, file, key, value or service to see it in both snapshots.";

    public DetailsPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = UiFonts.Normal;
        TabStop = false;
    }

    /// <summary>The item shown (tests).</summary>
    internal string Title => _title;

    /// <summary>The properties shown (tests).</summary>
    internal IReadOnlyList<ComparisonData.DetailRow> Rows => _rows;

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    /// <summary>Shows one item.</summary>
    public void ShowItem(string title, string status, Color? statusColor, string path, string leftHeader, string rightHeader, IReadOnlyList<ComparisonData.DetailRow> rows)
    {
        _title = title;
        _status = status;
        _statusColor = statusColor;
        _path = path;
        _leftHeader = leftHeader;
        _rightHeader = rightHeader;
        _rows = rows;
        _message = string.Empty;
        Invalidate();
    }

    /// <summary>Shows a line of text instead of an item ("3 items selected").</summary>
    public void ShowMessage(string message)
    {
        _title = string.Empty;
        _rows = [];
        _message = message;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _palette;
        g.Clear(p.Surface);
        using (var line = new Pen(p.Border))
        {
            g.DrawLine(line, 0, 0, Width, 0);
        }

        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var x = 16;
        var y = 10;
        var width = Math.Max(0, Width - 32);
        if (_message.Length > 0)
        {
            TextRenderer.DrawText(g, _message, Font, new Rectangle(x, y, width, LineHeight), p.SecondaryText, flags);
            return;
        }

        // Name · change · path
        var color = _statusColor ?? p.Text;
        var titleSize = TextRenderer.MeasureText(g, _title, UiFonts.Bold, new Size(width, LineHeight), flags);
        var titleWidth = Math.Min(titleSize.Width, width / 2);
        TextRenderer.DrawText(g, _title, UiFonts.Bold, new Rectangle(x, y, titleWidth, LineHeight), color, flags);
        var cursor = x + titleWidth + 10;
        if (_status.Length > 0)
        {
            var pill = TextRenderer.MeasureText(g, _status, Font, new Size(200, LineHeight), flags).Width + 16;
            var box = new Rectangle(cursor, y + 2, pill, LineHeight - 4);
            using (var pen = new Pen(color))
            {
                g.DrawRectangle(pen, box);
            }

            TextRenderer.DrawText(g, _status, Font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            cursor += pill + 10;
        }

        TextRenderer.DrawText(g, _path, Font, new Rectangle(cursor, y, Math.Max(0, x + width - cursor), LineHeight), p.SecondaryText, flags | TextFormatFlags.PathEllipsis);
        y += LineHeight + 6;
        if (_rows.Count == 0)
        {
            return;
        }

        // Property · left snapshot · right snapshot
        var column = Math.Max(80, (width - LabelWidth) / 2);
        var leftX = x + LabelWidth;
        var rightX = leftX + column;
        TextRenderer.DrawText(g, _leftHeader, Font, new Rectangle(leftX + 8, y, column - 16, LineHeight), p.SecondaryText, flags);
        TextRenderer.DrawText(g, _rightHeader, Font, new Rectangle(rightX + 8, y, column - 16, LineHeight), p.SecondaryText, flags);
        y += LineHeight;
        using var chip = new SolidBrush(Palette.Blend(p.Modified, p.Surface, p.IsDark ? 0.78 : 0.84));
        foreach (var row in _rows)
        {
            if (y + LineHeight > Height)
            {
                break;
            }

            TextRenderer.DrawText(g, row.Label, Font, new Rectangle(x, y, LabelWidth - 8, LineHeight), p.SecondaryText, flags);
            if (row.Changed)
            {
                g.FillRectangle(chip, leftX + 2, y + 2, column - 6, LineHeight - 4);
                g.FillRectangle(chip, rightX + 2, y + 2, column - 6, LineHeight - 4);
            }

            TextRenderer.DrawText(g, row.Left, Font, new Rectangle(leftX + 8, y, column - 16, LineHeight), p.Text, flags);
            TextRenderer.DrawText(g, row.Right, row.Changed ? UiFonts.Bold : Font, new Rectangle(rightX + 8, y, column - 16, LineHeight), p.Text, flags);
            y += LineHeight;
        }
    }
}
