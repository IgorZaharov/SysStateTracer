using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// The comparison window's details: the selected item's name, change and path, then its properties in the left
/// and right snapshot side by side; values that differ are highlighted. A value too long for one line wraps
/// onto up to three; the last ends with an ellipsis if it is still too long.
/// </summary>
internal sealed class DetailsPanel : Control, IThemed
{
    private const int LabelWidth = 130;
    private const int LineHeight = 22;

    /// <summary>A long value (a Path variable) takes up to this many lines.</summary>
    internal const int MaxLines = 3;
    private Palette _palette = ThemeManager.Current;
    private string _title = string.Empty;
    private string _status = string.Empty;
    private Color? _statusColor;
    private string _path = string.Empty;
    private (string Caption, string Name) _leftHeader = (string.Empty, string.Empty);
    private (string Caption, string Name) _rightHeader = (string.Empty, string.Empty);
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

    /// <summary>The column headers: "OLDER" or "NEWER" and the snapshot name (tests).</summary>
    internal ((string Caption, string Name) Left, (string Caption, string Name) Right) Headers => (_leftHeader, _rightHeader);

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    /// <summary>Shows one item. Each header is a caption ("OLDER", issue #6) and the snapshot name.</summary>
    public void ShowItem(string title, string status, Color? statusColor, string path, (string Caption, string Name) leftHeader, (string Caption, string Name) rightHeader, IReadOnlyList<ComparisonData.DetailRow> rows)
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
        DrawHeader(g, _leftHeader, new Rectangle(leftX + 8, y, column - 16, LineHeight), flags);
        DrawHeader(g, _rightHeader, new Rectangle(rightX + 8, y, column - 16, LineHeight), flags);
        y += LineHeight;
        using var chip = new SolidBrush(Palette.Blend(p.Modified, p.Surface, p.IsDark ? 0.78 : 0.84));
        var textWidth = column - 16;
        foreach (var row in _rows)
        {
            if (y + LineHeight > Height)
            {
                break;
            }

            var rightFont = row.Changed ? UiFonts.Bold : Font;
            var left = Wrap(row.Left, Font, textWidth);
            var right = Wrap(row.Right, rightFont, textWidth);
            var lines = Math.Max(left.Count, right.Count);
            var height = (lines * LineHeight) - ((lines - 1) * 4);
            TextRenderer.DrawText(g, row.Label, Font, new Rectangle(x, y, LabelWidth - 8, LineHeight), p.SecondaryText, flags);
            if (row.Changed)
            {
                g.FillRectangle(chip, leftX + 2, y + 2, column - 6, height - 4);
                g.FillRectangle(chip, rightX + 2, y + 2, column - 6, height - 4);
            }

            DrawLines(g, left, Font, new Rectangle(leftX + 8, y, textWidth, LineHeight), flags);
            DrawLines(g, right, rightFont, new Rectangle(rightX + 8, y, textWidth, LineHeight), flags);
            y += height;
        }
    }

    /// <summary>The lines of a value; each further line starts 4 pixels higher than a new row would.</summary>
    private void DrawLines(Graphics g, IReadOnlyList<string> lines, Font font, Rectangle line, TextFormatFlags flags)
    {
        foreach (var text in lines)
        {
            TextRenderer.DrawText(g, text, font, line, _palette.Text, flags);
            line.Offset(0, LineHeight - 4);
        }
    }

    /// <summary>
    /// Splits a value that is wider than <paramref name="width"/> into at most <see cref="MaxLines"/> lines, each as
    /// long as fits, broken after a ";", "\", "," or space when there is one in its last half (a Path variable breaks
    /// between folders). The last line holds the rest and is cut with an ellipsis when drawn.
    /// </summary>
    internal static IReadOnlyList<string> Wrap(string text, Font font, int width)
    {
        var lines = new List<string>(MaxLines);
        while (lines.Count < MaxLines - 1)
        {
            var (first, rest) = Split(text, font, width);
            lines.Add(first);
            if (rest is null)
            {
                return lines;
            }

            text = rest;
        }

        lines.Add(text);
        return lines;
    }

    /// <summary>The start of a text that fits in <paramref name="width"/>, and the rest (null when it all fits).</summary>
    private static (string First, string? Remainder) Split(string text, Font font, int width)
    {
        const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int Width(int length) => TextRenderer.MeasureText(text[..length], font, new Size(int.MaxValue, int.MaxValue), measure).Width;
        if (width <= 0 || text.Length < 2 || Width(text.Length) <= width)
        {
            return (text, null);
        }

        // The longest start that fits.
        int low = 1, high = text.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Width(mid) <= width)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var cut = low;
        var breakAt = text.LastIndexOfAny([';', '\\', ',', ' '], cut - 1);
        if (breakAt >= cut / 2)
        {
            cut = breakAt + 1;
        }

        return (text[..cut], text[cut..].TrimStart(' '));
    }

    /// <summary>"OLDER" in bold, then the snapshot name; the name is cut with an ellipsis when narrow.</summary>
    private void DrawHeader(Graphics g, (string Caption, string Name) header, Rectangle box, TextFormatFlags flags)
    {
        var x = box.X;
        if (header.Caption.Length > 0)
        {
            var width = Math.Min(box.Width, TextRenderer.MeasureText(g, header.Caption, UiFonts.Bold, box.Size, flags).Width);
            TextRenderer.DrawText(g, header.Caption, UiFonts.Bold, new Rectangle(x, box.Y, width, box.Height), _palette.SecondaryText, flags);
            x += width + 6;
        }

        TextRenderer.DrawText(g, header.Name, Font, new Rectangle(x, box.Y, Math.Max(0, box.Right - x), box.Height), _palette.SecondaryText, flags);
    }
}
