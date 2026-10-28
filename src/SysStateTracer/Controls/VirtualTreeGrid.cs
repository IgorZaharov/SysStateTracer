using System.Runtime.InteropServices;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>How one row is drawn.</summary>
/// <summary>How a row is drawn; <paramref name="Icon"/> is a glyph drawn before the name (F21).</summary>
internal readonly record struct GridRowStyle(Color? TextColor, bool Strikeout, bool Dim, bool Bold, string? Icon = null);

/// <summary>What <see cref="VirtualTreeGrid"/> shows: a tree of row ids with text per column, read on demand.</summary>
internal interface ITreeGridModel
{
    IReadOnlyList<long> Roots { get; }

    bool HasChildren(long id);

    /// <summary>Children in display order; called when a row is expanded.</summary>
    IReadOnlyList<long> Children(long id);

    string Text(long id, int column);

    string? ToolTip(long id, int column);

    GridRowStyle Style(long id);
}

/// <summary>A grid column: header, width, alignment.</summary>
internal sealed class GridColumn(string header, int width, HorizontalAlignment align = HorizontalAlignment.Left)
{
    public string Header { get; set; } = header;

    public int Width { get; set; } = width;

    public HorizontalAlignment Align { get; } = align;
}

/// <summary>
/// The comparison tree list (plan §9.4, Step 50). It keeps a flat list of the visible row ids: expanding
/// inserts the children's ids, collapsing removes them. Only the rows on screen are painted, with fonts and
/// brushes made once; no per-row controls, no animation. Selection (Ctrl/Shift) is a set of row ids, so
/// selecting thousands of rows is instant. Scrollbars are the window's own, themed dark with the window.
/// </summary>
internal sealed partial class VirtualTreeGrid : Control, IThemed
{
    private const int WsVScroll = 0x00200000;
    private const int WsHScroll = 0x00100000;
    private const int WmVScroll = 0x0115;
    private const int WmHScroll = 0x0114;
    private const int SbHorz = 0;
    private const int SbVert = 1;
    private const int Indent = 18;
    private const int IconWidth = 16;

    private readonly List<long> _rows = [];
    private readonly List<int> _depth = [];
    private readonly HashSet<long> _expanded = [];
    private readonly HashSet<long> _selected = [];
    private readonly ThemedToolTip _toolTip = new() { ShowAlways = false };
    private readonly Font _glyphFont = UiFonts.Glyph(8F);
    private readonly Font _iconFont = UiFonts.Glyph(10F);
    private Palette _palette = ThemeManager.Current;
    private ITreeGridModel? _model;
    private int _top;
    private int _left;
    private int _focus = -1;
    private int _anchor = -1;
    private int _resizingColumn = -1;
    private int _resizeStartX;
    private int _resizeStartWidth;
    private (int Row, int Column) _toolTipCell = (-1, -1);

    public VirtualTreeGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        Font = UiFonts.Normal;
        RowHeight = 22;
    }

    /// <summary>Raised when the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised for Enter or a double-click on a row without children.</summary>
    public event EventHandler<long>? RowActivated;

    public List<GridColumn> Columns { get; } = [];

    public int RowHeight { get; set; }

    /// <summary>Show the model's tool tips on hover; off in the comparison window, where they covered the next rows (F19).</summary>
    public bool ShowToolTips { get; set; } = true;

    public int HeaderHeight => RowHeight + 4;

    /// <summary>Visible rows (expanded state applied).</summary>
    public int RowCount => _rows.Count;

    public IReadOnlyCollection<long> SelectedIds => _selected;

    /// <summary>Ids of the visible rows, top to bottom.</summary>
    public IReadOnlyList<long> VisibleIds => _rows;

    /// <summary>Index of the first row on screen.</summary>
    public int TopRow => _top;

    public long? FocusedId => _focus >= 0 && _focus < _rows.Count ? _rows[_focus] : null;

    public ITreeGridModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            Rebuild(keepState: false);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WsVScroll | WsHScroll;
            return cp;
        }
    }

    private int VisibleRowCount => Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);

    private int TotalWidth => Columns.Sum(c => c.Width);

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        if (IsHandleCreated)
        {
            NativeUi.SetExplorerTheme(Handle, palette.IsDark);
        }

        Invalidate();
    }

    /// <summary>Rebuilds the visible rows from the model, keeping expanded rows and the selection when asked.</summary>
    public void Rebuild(bool keepState)
    {
        var focused = FocusedId;
        if (!keepState)
        {
            _expanded.Clear();
            _selected.Clear();
            focused = null;
        }

        _rows.Clear();
        _depth.Clear();
        if (_model is not null)
        {
            foreach (var root in _model.Roots)
            {
                AppendRow(root, 0);
            }
        }

        _selected.IntersectWith(_rows);
        _focus = focused is { } id ? _rows.IndexOf(id) : (_rows.Count > 0 ? 0 : -1);
        _anchor = _focus;
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - VisibleRowCount));
        UpdateScrollBars();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsExpanded(long id) => _expanded.Contains(id);

    /// <summary>Expands the visible row with this id.</summary>
    public void Expand(long id)
    {
        var index = _rows.IndexOf(id);
        if (index >= 0)
        {
            ExpandAt(index);
        }
    }

    public void Collapse(long id)
    {
        var index = _rows.IndexOf(id);
        if (index >= 0)
        {
            CollapseAt(index);
        }
    }

    public void SelectAll()
    {
        _selected.UnionWith(_rows);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects these rows (those that are visible) without scrolling; the first becomes the focused row.</summary>
    public void SetSelection(IEnumerable<long> ids)
    {
        _selected.Clear();
        _selected.UnionWith(ids);
        _selected.IntersectWith(_rows);
        _focus = _selected.Count > 0 ? _rows.FindIndex(_selected.Contains) : (_rows.Count > 0 ? 0 : -1);
        _anchor = _focus;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects one row (and scrolls to it).</summary>
    public void Select(long id)
    {
        var index = _rows.IndexOf(id);
        if (index >= 0)
        {
            SetFocus(index, extend: false, toggle: false);
        }
    }

    /// <summary>Paints the rows that would be on screen into <paramref name="g"/> (benchmark of one frame).</summary>
    internal void PaintFrame(Graphics g) => PaintAll(g);

    /// <summary>Scrolls so that <paramref name="row"/> is the first row shown.</summary>
    internal void ScrollTo(int row)
    {
        _top = Math.Clamp(row, 0, Math.Max(0, _rows.Count - VisibleRowCount));
        UpdateScrollBars();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetExplorerTheme(Handle, _palette.IsDark);
        UpdateScrollBars();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - VisibleRowCount));
        _left = Math.Clamp(_left, 0, Math.Max(0, TotalWidth - ClientSize.Width));
        UpdateScrollBars();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End => true,
        _ => base.IsInputKey(keyData),
    };

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0)
        {
            return;
        }

        var extend = e.Shift;
        var focus = Math.Max(0, _focus);
        switch (e.KeyCode)
        {
            case Keys.Up:
                SetFocus(focus - 1, extend, false);
                break;
            case Keys.Down:
                SetFocus(focus + 1, extend, false);
                break;
            case Keys.PageUp:
                SetFocus(focus - VisibleRowCount + 1, extend, false);
                break;
            case Keys.PageDown:
                SetFocus(focus + VisibleRowCount - 1, extend, false);
                break;
            case Keys.Home:
                SetFocus(0, extend, false);
                break;
            case Keys.End:
                SetFocus(_rows.Count - 1, extend, false);
                break;
            case Keys.Right:
                if (_model?.HasChildren(_rows[focus]) == true && !_expanded.Contains(_rows[focus]))
                {
                    ExpandAt(focus);
                }
                else if (focus + 1 < _rows.Count && _depth[focus + 1] > _depth[focus])
                {
                    SetFocus(focus + 1, false, false);
                }

                break;
            case Keys.Left:
                if (_expanded.Contains(_rows[focus]))
                {
                    CollapseAt(focus);
                }
                else
                {
                    var parent = ParentIndex(focus);
                    if (parent >= 0)
                    {
                        SetFocus(parent, false, false);
                    }
                }

                break;
            case Keys.A when e.Control:
                SelectAll();
                break;
            case Keys.Space when e.Control:
                SetFocus(focus, false, toggle: true);
                break;
            case Keys.Enter:
                Activate(focus);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Y < HeaderHeight)
        {
            var edge = ColumnEdgeAt(e.X);
            if (edge >= 0 && e.Button == MouseButtons.Left)
            {
                _resizingColumn = edge;
                _resizeStartX = e.X;
                _resizeStartWidth = Columns[edge].Width;
                Capture = true;
            }

            return;
        }

        var row = RowAt(e.Y);
        if (row < 0)
        {
            return;
        }

        if (e.Button == MouseButtons.Left && ExpanderHit(row, e.X))
        {
            Toggle(row);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(_rows[row]))
            {
                SetFocus(row, false, false);
            }

            return;
        }

        SetFocus(row, extend: (ModifierKeys & Keys.Shift) != 0, toggle: (ModifierKeys & Keys.Control) != 0);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_resizingColumn >= 0)
        {
            _resizingColumn = -1;
            Capture = false;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizingColumn >= 0)
        {
            Columns[_resizingColumn].Width = Math.Max(40, _resizeStartWidth + e.X - _resizeStartX);
            UpdateScrollBars();
            Invalidate();
            return;
        }

        Cursor = e.Y < HeaderHeight && ColumnEdgeAt(e.X) >= 0 ? Cursors.VSplit : Cursors.Default;
        UpdateToolTip(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _toolTip.Hide(this);
        _toolTipCell = (-1, -1);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var row = RowAt(e.Y);
        if (row >= 0 && e.Button == MouseButtons.Left && !ExpanderHit(row, e.X))
        {
            Activate(row);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var lines = SystemInformation.MouseWheelScrollLines;
        var delta = -e.Delta / 120 * (lines <= 0 ? 3 : lines);
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            ScrollHorizontally(delta * 20);
        }
        else
        {
            ScrollTo(_top + delta);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmVScroll)
        {
            OnScroll(SbVert, (int)(m.WParam & 0xFFFF));
            return;
        }

        if (m.Msg == WmHScroll)
        {
            OnScroll(SbHorz, (int)(m.WParam & 0xFFFF));
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e) => PaintAll(e.Graphics);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _glyphFont.Dispose();
            _iconFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AppendRow(long id, int depth)
    {
        _rows.Add(id);
        _depth.Add(depth);
        if (_expanded.Contains(id) && _model is not null)
        {
            if (!_model.HasChildren(id))
            {
                _expanded.Remove(id);
                return;
            }

            foreach (var child in _model.Children(id))
            {
                AppendRow(child, depth + 1);
            }
        }
    }

    private void Toggle(int index)
    {
        if (_expanded.Contains(_rows[index]))
        {
            CollapseAt(index);
        }
        else
        {
            ExpandAt(index);
        }
    }

    private void ExpandAt(int index)
    {
        var before = _rows.Count;
        if (!ExpandCore(index))
        {
            return;
        }

        var added = _rows.Count - before;
        if (_focus > index)
        {
            _focus += added;
        }

        if (_anchor > index)
        {
            _anchor += added;
        }

        UpdateScrollBars();
        Invalidate();
    }

    /// <summary>Inserts the children of the row (and of children that were expanded before).</summary>
    private bool ExpandCore(int index)
    {
        var id = _rows[index];
        if (_model is null || !_model.HasChildren(id) || !_expanded.Add(id))
        {
            return false;
        }

        var children = _model.Children(id);
        var depth = _depth[index] + 1;
        _rows.InsertRange(index + 1, children);
        _depth.InsertRange(index + 1, Enumerable.Repeat(depth, children.Count));
        for (var i = index + children.Count; i > index; i--)
        {
            if (_expanded.Remove(_rows[i]))
            {
                ExpandCore(i);
            }
        }

        return true;
    }

    private void CollapseAt(int index)
    {
        var id = _rows[index];
        if (!_expanded.Remove(id))
        {
            return;
        }

        var end = index + 1;
        var depth = _depth[index];
        while (end < _rows.Count && _depth[end] > depth)
        {
            end++;
        }

        var count = end - index - 1;
        if (count > 0)
        {
            for (var i = index + 1; i < end; i++)
            {
                _selected.Remove(_rows[i]);
            }

            _rows.RemoveRange(index + 1, count);
            _depth.RemoveRange(index + 1, count);
        }

        if (_focus > index && _focus < end)
        {
            _focus = index;
            _selected.Add(id);
        }
        else if (_focus >= end)
        {
            _focus -= count;
        }

        _anchor = Math.Min(_anchor, _rows.Count - 1);
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - VisibleRowCount));
        UpdateScrollBars();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int ParentIndex(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (_depth[i] < _depth[index])
            {
                return i;
            }
        }

        return -1;
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        if (_model?.HasChildren(_rows[index]) == true)
        {
            Toggle(index);
        }
        else
        {
            RowActivated?.Invoke(this, _rows[index]);
        }
    }

    private void SetFocus(int index, bool extend, bool toggle)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (extend && _anchor >= 0)
        {
            _selected.Clear();
            var (from, to) = _anchor <= index ? (_anchor, index) : (index, _anchor);
            for (var i = from; i <= to; i++)
            {
                _selected.Add(_rows[i]);
            }
        }
        else if (toggle)
        {
            if (!_selected.Remove(_rows[index]))
            {
                _selected.Add(_rows[index]);
            }

            _anchor = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(_rows[index]);
            _anchor = index;
        }

        _focus = index;
        if (index < _top)
        {
            _top = index;
        }
        else if (index >= _top + VisibleRowCount)
        {
            _top = index - VisibleRowCount + 1;
        }

        UpdateScrollBars();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int RowAt(int y)
    {
        if (y < HeaderHeight)
        {
            return -1;
        }

        var row = _top + ((y - HeaderHeight) / RowHeight);
        return row < _rows.Count ? row : -1;
    }

    private bool ExpanderHit(int row, int x)
    {
        if (_model?.HasChildren(_rows[row]) != true)
        {
            return false;
        }

        var start = 4 + (_depth[row] * Indent) - _left;
        return x >= start - 2 && x < start + Indent;
    }

    private int ColumnEdgeAt(int x)
    {
        var edge = -_left;
        for (var i = 0; i < Columns.Count; i++)
        {
            edge += Columns[i].Width;
            if (Math.Abs(x - edge) <= 3)
            {
                return i;
            }
        }

        return -1;
    }

    private void ScrollHorizontally(int delta)
    {
        _left = Math.Clamp(_left + delta, 0, Math.Max(0, TotalWidth - ClientSize.Width));
        UpdateScrollBars();
        Invalidate();
    }

    private void OnScroll(int bar, int request)
    {
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 /* SIF_ALL */ };
        _ = GetScrollInfo(Handle, bar, ref info);
        var page = bar == SbVert ? VisibleRowCount : ClientSize.Width;
        var line = bar == SbVert ? 1 : 20;
        var position = bar == SbVert ? _top : _left;
        position = request switch
        {
            0 => position - line,          // SB_LINEUP
            1 => position + line,          // SB_LINEDOWN
            2 => position - page,          // SB_PAGEUP
            3 => position + page,          // SB_PAGEDOWN
            4 or 5 => info.TrackPos,       // SB_THUMBPOSITION, SB_THUMBTRACK
            6 => 0,                        // SB_TOP
            7 => int.MaxValue / 2,         // SB_BOTTOM
            _ => position,
        };
        if (bar == SbVert)
        {
            ScrollTo(position);
        }
        else
        {
            _left = Math.Clamp(position, 0, Math.Max(0, TotalWidth - ClientSize.Width));
            UpdateScrollBars();
            Invalidate();
        }
    }

    private void UpdateScrollBars()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var vertical = new ScrollInfo
        {
            Size = (uint)Marshal.SizeOf<ScrollInfo>(),
            Mask = 0x1 | 0x2 | 0x4 | 0x8, // SIF_RANGE | SIF_PAGE | SIF_POS | SIF_DISABLENOSCROLL
            Min = 0,
            Max = Math.Max(0, _rows.Count - 1),
            Page = (uint)VisibleRowCount,
            Pos = _top,
        };
        _ = SetScrollInfo(Handle, SbVert, ref vertical, true);
        var horizontal = new ScrollInfo
        {
            Size = (uint)Marshal.SizeOf<ScrollInfo>(),
            Mask = 0x1 | 0x2 | 0x4,
            Min = 0,
            Max = Math.Max(0, TotalWidth - 1),
            Page = (uint)Math.Max(1, ClientSize.Width),
            Pos = _left,
        };
        _ = SetScrollInfo(Handle, SbHorz, ref horizontal, true);
    }

    private void UpdateToolTip(Point location)
    {
        if (!ShowToolTips)
        {
            return;
        }

        var row = RowAt(location.Y);
        var column = -1;
        var x = -_left;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (location.X >= x && location.X < x + Columns[i].Width)
            {
                column = i;
                break;
            }

            x += Columns[i].Width;
        }

        if ((row, column) == _toolTipCell)
        {
            return;
        }

        _toolTipCell = (row, column);
        _toolTip.Hide(this);
        if (row < 0 || column < 0 || _model is null)
        {
            return;
        }

        var tip = _model.ToolTip(_rows[row], column);
        if (string.IsNullOrEmpty(tip))
        {
            return;
        }

        var y = HeaderHeight + ((row - _top + 1) * RowHeight) + 2;
        _toolTip.Show(tip, this, location.X + 12, y, 15_000);
    }

    private void PaintAll(Graphics g)
    {
        var p = _palette;
        g.Clear(p.Background);
        if (Columns.Count == 0)
        {
            return;
        }

        var flagsLeft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var flagsRight = (flagsLeft & ~TextFormatFlags.Left) | TextFormatFlags.Right;

        // Rows on screen only.
        var last = Math.Min(_rows.Count, _top + VisibleRowCount + 1);
        using var selected = new SolidBrush(Focused ? p.Selection : p.SelectionInactive);
        using var focusPen = new Pen(p.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (var row = _top; row < last; row++)
        {
            var id = _rows[row];
            var y = HeaderHeight + ((row - _top) * RowHeight);
            var rect = new Rectangle(0, y, ClientSize.Width, RowHeight);
            if (_selected.Contains(id))
            {
                g.FillRectangle(selected, rect);
            }

            if (row == _focus && Focused)
            {
                g.DrawRectangle(focusPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }

            var style = _model!.Style(id);
            var color = style.TextColor ?? p.Text;
            if (style.Dim)
            {
                color = Palette.Blend(color, p.Background, 0.5);
            }

            var font = style.Strikeout ? UiFonts.Strikeout : style.Bold ? UiFonts.Bold : Font;
            var x = -_left;
            for (var c = 0; c < Columns.Count; c++)
            {
                var column = Columns[c];
                var cell = new Rectangle(x + 6, y, column.Width - 12, RowHeight);
                if (c == 0)
                {
                    var indent = 4 + (_depth[row] * Indent);
                    if (_model.HasChildren(id))
                    {
                        var chevron = _expanded.Contains(id) ? "\uE70D" : "\uE76C";
                        TextRenderer.DrawText(g, chevron, _glyphFont, new Rectangle(x + indent, y, Indent, RowHeight), p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }

                    var textLeft = indent + Indent + 2;
                    if (style.Icon is { } icon)
                    {
                        // One glyph per row on screen: no measurable cost (the frame-time test covers it).
                        TextRenderer.DrawText(g, icon, _iconFont, new Rectangle(x + textLeft, y, IconWidth, RowHeight), style.Dim ? color : p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        textLeft += IconWidth + 4;
                    }

                    cell = new Rectangle(x + textLeft, y, column.Width - textLeft - 6, RowHeight);
                }

                if (cell.Right > 0 && cell.Left < ClientSize.Width && cell.Width > 0)
                {
                    var text = _model.Text(id, c);
                    if (text.Length > 0)
                    {
                        // New, modified and deleted colours go through every column (F27).
                        TextRenderer.DrawText(g, text, font, cell, color, column.Align == HorizontalAlignment.Right ? flagsRight : flagsLeft);
                    }
                }

                x += column.Width;
            }
        }

        // Header.
        using (var headerBrush = new SolidBrush(p.Background))
        {
            g.FillRectangle(headerBrush, 0, 0, ClientSize.Width, HeaderHeight);
        }

        using var border = new Pen(p.Border);
        g.DrawLine(border, 0, HeaderHeight - 1, ClientSize.Width, HeaderHeight - 1);
        var hx = -_left;
        foreach (var column in Columns)
        {
            var rect = new Rectangle(hx + 6, 0, column.Width - 12, HeaderHeight);
            TextRenderer.DrawText(g, column.Header, Font, rect, p.SecondaryText, column.Align == HorizontalAlignment.Right ? flagsRight : flagsLeft);
            hx += column.Width;
            g.DrawLine(border, hx - 1, 5, hx - 1, HeaderHeight - 6);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint Size;
        public uint Mask;
        public int Min;
        public int Max;
        public uint Page;
        public int Pos;
        public int TrackPos;
    }

    [LibraryImport("user32.dll")]
    private static partial int SetScrollInfo(nint hwnd, int bar, ref ScrollInfo info, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetScrollInfo(nint hwnd, int bar, ref ScrollInfo info);
}
