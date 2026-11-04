using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// The comparison window's details: the selected item's name, change and path, then its properties in the left
/// and right snapshot side by side; values that differ are highlighted. A value too long for one line wraps onto
/// up to three; a longer one shows three at a time with a scroll bar beside it (mouse wheel, click or drag).
/// </summary>
internal sealed class DetailsPanel : Control, IThemed
{
    private const int LabelWidth = 130;
    private const int LineHeight = 22;
    private const int LineStep = LineHeight - 4;
    private const int BarWidth = 6;

    /// <summary>A long value (a Path variable) shows this many lines at a time; more scroll.</summary>
    internal const int MaxLines = 3;

    private readonly Dictionary<(int Row, bool Right), int> _scroll = [];
    private readonly Dictionary<(string Text, int Width, bool Bold), IReadOnlyList<string>> _wrapped = [];
    private readonly List<Cell> _cells = [];
    private Palette _palette = ThemeManager.Current;
    private string _title = string.Empty;
    private string _status = string.Empty;
    private Color? _statusColor;
    private string _path = string.Empty;
    private (string Caption, string Name) _leftHeader = (string.Empty, string.Empty);
    private (string Caption, string Name) _rightHeader = (string.Empty, string.Empty);
    private IReadOnlyList<ComparisonData.DetailRow> _rows = [];
    private string _message = "Select a folder, file, key, value or service to see it in both snapshots.";
    private (Cell Cell, int Grab)? _drag;

    public DetailsPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = UiFonts.Normal;
        TabStop = false;
    }

    /// <summary>One value as last drawn: where it is, its scroll bar (empty when it fits) and its number of lines.</summary>
    internal readonly record struct Cell(int Row, bool Right, Rectangle Bounds, Rectangle Track, int Lines);

    /// <summary>The item shown (tests).</summary>
    internal string Title => _title;

    /// <summary>The properties shown (tests).</summary>
    internal IReadOnlyList<ComparisonData.DetailRow> Rows => _rows;

    /// <summary>The values as last drawn (tests).</summary>
    internal IReadOnlyList<Cell> Cells => _cells;

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
        _scroll.Clear();
        _wrapped.Clear();
        _drag = null;
        Invalidate();
    }

    /// <summary>Shows a line of text instead of an item ("3 items selected").</summary>
    public void ShowMessage(string message)
    {
        _title = string.Empty;
        _rows = [];
        _message = message;
        _cells.Clear();
        _drag = null;
        Invalidate();
    }

    /// <summary>The first line shown of a value (0 unless it was scrolled).</summary>
    internal int ScrollOf(int row, bool right) => _scroll.GetValueOrDefault((row, right));

    /// <summary>Scrolls a value by <paramref name="lines"/> (negative: up), within its lines; false when it does not scroll.</summary>
    internal bool ScrollBy(int row, bool right, int lines)
    {
        var cell = _cells.FindIndex(c => c.Row == row && c.Right == right);
        return cell >= 0 && ScrollTo(_cells[cell], ScrollOf(row, right) + lines);
    }

    private bool ScrollTo(Cell cell, int first)
    {
        if (cell.Lines <= MaxLines)
        {
            return false;
        }

        first = Math.Clamp(first, 0, cell.Lines - MaxLines);
        if (first != ScrollOf(cell.Row, cell.Right))
        {
            _scroll[(cell.Row, cell.Right)] = first;
            Invalidate();
        }

        return true;
    }

    private Cell? CellAt(Point point)
    {
        foreach (var cell in _cells)
        {
            if (cell.Bounds.Contains(point))
            {
                return cell;
            }
        }

        return null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (CellAt(e.Location) is { } cell && ScrollTo(cell, ScrollOf(cell.Row, cell.Right) - Math.Sign(e.Delta)) && e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || CellAt(e.Location) is not { } cell || cell.Lines <= MaxLines || !cell.Track.Contains(e.Location))
        {
            return;
        }

        var thumb = Thumb(cell);
        if (thumb.Contains(e.Location))
        {
            _drag = (cell, e.Y - thumb.Top);
        }
        else
        {
            // A click on the track pages towards it.
            ScrollTo(cell, ScrollOf(cell.Row, cell.Right) + (e.Y < thumb.Top ? -MaxLines : MaxLines));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } drag)
        {
            return;
        }

        var cell = drag.Cell;
        var thumb = Thumb(cell);
        var room = Math.Max(1, cell.Track.Height - thumb.Height);
        var top = Math.Clamp(e.Y - drag.Grab - cell.Track.Top, 0, room);
        ScrollTo(cell, (int)Math.Round(top * (double)(cell.Lines - MaxLines) / room));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
    }

    /// <summary>The scroll bar's thumb: as tall as the share of lines shown, where the first line shown is.</summary>
    private Rectangle Thumb(Cell cell)
    {
        var track = cell.Track;
        var height = Math.Max(12, track.Height * MaxLines / cell.Lines);
        var room = track.Height - height;
        var top = track.Top + (int)Math.Round(room * (double)ScrollOf(cell.Row, cell.Right) / (cell.Lines - MaxLines));
        return new Rectangle(track.X, top, track.Width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _palette;
        g.Clear(p.Surface);
        _cells.Clear();
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
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (y + LineHeight > Height)
            {
                break;
            }

            var rightFont = row.Changed ? UiFonts.Bold : Font;
            var left = Lines(row.Left, Font, bold: false, textWidth);
            var right = Lines(row.Right, rightFont, row.Changed, textWidth);
            var lines = Math.Min(MaxLines, Math.Max(left.Count, right.Count));
            var height = (lines * LineHeight) - ((lines - 1) * 4);
            TextRenderer.DrawText(g, row.Label, Font, new Rectangle(x, y, LabelWidth - 8, LineHeight), p.SecondaryText, flags);
            if (row.Changed)
            {
                g.FillRectangle(chip, leftX + 2, y + 2, column - 6, height - 4);
                g.FillRectangle(chip, rightX + 2, y + 2, column - 6, height - 4);
            }

            DrawCell(g, new Cell(r, false, new Rectangle(leftX, y, column, height), Track(leftX, y, column, height, left.Count), left.Count), left, Font, textWidth, flags);
            DrawCell(g, new Cell(r, true, new Rectangle(rightX, y, column, height), Track(rightX, y, column, height, right.Count), right.Count), right, rightFont, textWidth, flags);
            y += height;
        }
    }

    /// <summary>A value's lines, wrapped to the column, or narrower to leave room for a scroll bar when they are more than three.</summary>
    private IReadOnlyList<string> Lines(string text, Font font, bool bold, int width)
    {
        if (_wrapped.Count > 64)
        {
            _wrapped.Clear(); // resized many times
        }

        if (!_wrapped.TryGetValue((text, width, bold), out var lines))
        {
            lines = Wrap(text, font, width);
            if (lines.Count > MaxLines)
            {
                lines = Wrap(text, font, width - BarWidth - 6);
            }

            _wrapped[(text, width, bold)] = lines;
        }

        return lines;
    }

    private static Rectangle Track(int columnX, int y, int column, int height, int lines) =>
        lines > MaxLines ? new Rectangle(columnX + column - BarWidth - 6, y + 3, BarWidth, height - 6) : Rectangle.Empty;

    /// <summary>The lines shown of a value, from where it is scrolled to, and its scroll bar.</summary>
    private void DrawCell(Graphics g, Cell cell, IReadOnlyList<string> lines, Font font, int width, TextFormatFlags flags)
    {
        _cells.Add(cell);
        var first = Math.Clamp(ScrollOf(cell.Row, cell.Right), 0, Math.Max(0, lines.Count - MaxLines));
        var line = new Rectangle(cell.Bounds.X + 8, cell.Bounds.Y, cell.Lines > MaxLines ? width - BarWidth - 6 : width, LineHeight);
        for (var i = first; i < Math.Min(lines.Count, first + MaxLines); i++)
        {
            TextRenderer.DrawText(g, lines[i], font, line, _palette.Text, flags);
            line.Offset(0, LineStep);
        }

        if (cell.Lines > MaxLines)
        {
            using var track = new SolidBrush(Palette.Blend(_palette.Border, _palette.Surface, 0.5));
            using var thumb = new SolidBrush(_palette.SecondaryText);
            g.FillRectangle(track, cell.Track);
            g.FillRectangle(thumb, Thumb(cell));
        }
    }

    /// <summary>
    /// Splits a value into lines no wider than <paramref name="width"/>, each as long as fits, broken after a ";",
    /// "\", "," or space when there is one in its last half (a Path variable breaks between folders).
    /// </summary>
    internal static IReadOnlyList<string> Wrap(string text, Font font, int width)
    {
        var lines = new List<string>();
        while (true)
        {
            var (first, rest) = Split(text, font, width);
            lines.Add(first);
            if (rest is null || rest.Length == 0)
            {
                return lines;
            }

            text = rest;
        }
    }

    /// <summary>The start of a text that fits in <paramref name="width"/>, and the rest (null when it all fits).</summary>
    private static (string First, string? Remainder) Split(string text, Font font, int width)
    {
        const TextFormatFlags measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int Width(int length) => TextRenderer.MeasureText(text[..length], font, new Size(int.MaxValue, int.MaxValue), measure).Width;

        // Every character is at least a pixel wide, so a line never holds more than width characters.
        if (width <= 0 || text.Length < 2 || (text.Length <= width && Width(text.Length) <= width))
        {
            return (text, null);
        }

        // The longest start that fits (at least one character, so a very narrow column still ends).
        int low = 1, high = Math.Min(text.Length - 1, width);
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
