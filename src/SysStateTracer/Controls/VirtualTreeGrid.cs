using System.Runtime.InteropServices;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// How a row is drawn; <paramref name="Icon"/> is a glyph drawn before the name (F21), in
/// <paramref name="IconColor"/> (secondary text when null).
/// </summary>
internal readonly record struct GridRowStyle(Color? TextColor, bool Strikeout, bool Dim, bool Bold, string? Icon = null, Color? IconColor = null);

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

    /// <summary>True for a cell whose value differs between the snapshots: drawn on a highlight.</summary>
    bool IsChangedCell(long id, int column) => false;
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

    // Per visible row, for tree lines: bit k = a line passes down at level k (k < depth); bit depth = a next sibling.
    private readonly List<ulong> _lines = [];
    private bool _linesDirty = true;

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

    /// <summary>Raised when a column header is clicked (not its edge); the argument is the column.</summary>
    public event EventHandler<int>? HeaderClicked;

    /// <summary>Column headers on top; off for a tree without columns.</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>Dotted lines from each row to its parent, and + / − boxes, instead of arrows.</summary>
    public bool TreeLines { get; set; }

    /// <summary>A plain list: no room for expanders before the first column's text.</summary>
    public bool Flat { get; set; }

    /// <summary>Grid lines and zebra stripes (Preferences) apply; off for the folder tree.</summary>
    public bool UseListStyle { get; set; } = true;

    /// <summary>The last column is as wide as the space left (at least its own width).</summary>
    public bool StretchLastColumn { get; set; }

    /// <summary>Column with the sort arrow in its header, or -1.</summary>
    public int SortColumn { get; set; } = -1;

    /// <summary>Arrow down (descending) or up.</summary>
    public bool SortDescending { get; set; }

    public List<GridColumn> Columns { get; } = [];

    public int RowHeight { get; set; }

    /// <summary>The column with the tree (indent, expander, icon); 0 unless a narrow column comes before it.</summary>
    public int TreeColumn { get; set; }

    /// <summary>Left edge of the tree column, before horizontal scrolling.</summary>
    private int TreeLeft => Columns.Take(TreeColumn).Sum(c => c.Width);

    /// <summary>Show the model's tool tips on hover; off in the comparison window, where they covered the next rows (F19).</summary>
    public bool ShowToolTips { get; set; } = true;

    public int HeaderHeight => ShowHeader ? RowHeight + 4 : 0;

    /// <summary>Room for the expander before a row's icon and text.</summary>
    private int ExpanderWidth => Flat ? 0 : Indent;

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

    private int TotalWidth => Columns.Sum(c => c.Width) + StretchExtra;

    /// <summary>Extra width the last column gets to fill the control.</summary>
    private int StretchExtra => StretchLastColumn && Columns.Count > 0 ? Math.Max(0, ClientSize.Width - Columns.Sum(c => c.Width)) : 0;

    private int ColumnWidth(int column) => Columns[column].Width + (column == Columns.Count - 1 ? StretchExtra : 0);

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

        _linesDirty = true;
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
            else if (e.Button == MouseButtons.Left && ColumnAt(e.X) is var column and >= 0)
            {
                HeaderClicked?.Invoke(this, column);
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

        _linesDirty = true;
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
            _linesDirty = true;
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

        if (Flat)
        {
            return false;
        }

        var start = TreeLeft + 4 + (_depth[row] * Indent) - _left;
        return x >= start - 2 && x < start + Indent;
    }

    /// <summary>The column at <paramref name="x"/>, or -1.</summary>
    private int ColumnAt(int x)
    {
        var left = -_left;
        for (var i = 0; i < Columns.Count; i++)
        {
            var width = ColumnWidth(i);
            if (x >= left && x < left + width)
            {
                return i;
            }

            left += width;
        }

        return -1;
    }

    /// <summary>
    /// Which dotted lines each row needs, in one pass from the bottom: a line at level k passes a row when the
    /// nearest following row at level k or above is at level k (the ancestor at that level has a next sibling).
    /// </summary>
    private void ComputeLines()
    {
        _lines.Clear();
        var maxDepth = 0;
        foreach (var d in _depth)
        {
            maxDepth = Math.Max(maxDepth, d);
        }

        var seen = new bool[maxDepth + 2];
        for (var i = 0; i < _rows.Count; i++)
        {
            _lines.Add(0);
        }

        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var depth = _depth[i];
            ulong mask = 0;
            for (var k = 0; k <= Math.Min(depth, 63); k++)
            {
                if (seen[k])
                {
                    mask |= 1UL << k;
                }
            }

            _lines[i] = mask;
            seen[depth] = true;
            for (var k = depth + 1; k < seen.Length; k++)
            {
                seen[k] = false;
            }
        }

        _linesDirty = false;
    }

    private int ColumnEdgeAt(int x)
    {
        var edge = -_left;
        for (var i = 0; i < Columns.Count; i++)
        {
            edge += ColumnWidth(i);
            if (Math.Abs(x - edge) <= 3 && !(StretchLastColumn && i == Columns.Count - 1))
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
            if (location.X >= x && location.X < x + ColumnWidth(i))
            {
                column = i;
                break;
            }

            x += ColumnWidth(i);
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

        if (TreeLines && _linesDirty)
        {
            ComputeLines();
        }

        var flagsLeft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var flagsRight = (flagsLeft & ~TextFormatFlags.Left) | TextFormatFlags.Right;

        // Rows on screen only.
        var last = Math.Min(_rows.Count, _top + VisibleRowCount + 1);
        using var selected = new SolidBrush(Focused ? p.Selection : p.SelectionInactive);
        using var focusPen = new Pen(p.Accent) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        using var stripe = new SolidBrush(p.Stripe);
        using var gridPen = new Pen(p.GridLine);
        using var linePen = new Pen(Palette.Blend(p.SecondaryText, p.Background, 0.35)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        using var boxPen = new Pen(Palette.Blend(p.SecondaryText, p.Background, 0.2));
        using var boxBrush = new SolidBrush(p.Background);
        using var signPen = new Pen(p.Text);
        using var chip = new SolidBrush(Palette.Blend(p.Modified, p.Background, p.IsDark ? 0.78 : 0.84));
        var gridLines = UseListStyle && ThemeManager.GridLines;
        var zebra = UseListStyle && ThemeManager.ZebraStripes;
        for (var row = _top; row < last; row++)
        {
            var id = _rows[row];
            var y = HeaderHeight + ((row - _top) * RowHeight);
            var rect = new Rectangle(0, y, ClientSize.Width, RowHeight);
            if (_selected.Contains(id))
            {
                g.FillRectangle(selected, rect);
            }
            else if (zebra && row % 2 == 1)
            {
                g.FillRectangle(stripe, rect);
            }

            if (gridLines)
            {
                g.DrawLine(gridPen, 0, y + RowHeight - 1, ClientSize.Width, y + RowHeight - 1);
                var line = -_left;
                for (var c = 0; c < Columns.Count - 1; c++)
                {
                    line += ColumnWidth(c);
                    g.DrawLine(gridPen, line - 1, y, line - 1, y + RowHeight - 1);
                }
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
                var width = ColumnWidth(c);
                var cell = new Rectangle(x + 6, y, width - 12, RowHeight);
                if (c == TreeColumn)
                {
                    var indent = 4 + (_depth[row] * Indent);
                    var hasChildren = !Flat && _model.HasChildren(id);
                    if (TreeLines)
                    {
                        DrawTreeLines(g, linePen, x + 4, y, row, hasChildren);
                        if (hasChildren)
                        {
                            DrawBox(g, boxPen, boxBrush, signPen, x + indent + (Indent / 2), y + (RowHeight / 2), _expanded.Contains(id));
                        }
                    }
                    else if (hasChildren)
                    {
                        var chevron = _expanded.Contains(id) ? "\uE70D" : "\uE76C";
                        TextRenderer.DrawText(g, chevron, _glyphFont, new Rectangle(x + indent, y, Indent, RowHeight), p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }

                    var textLeft = Flat ? 6 : indent + ExpanderWidth + 2;
                    if (style.Icon is { } icon)
                    {
                        // One glyph per row on screen: no measurable cost (the frame-time test covers it).
                        var iconColor = style.IconColor ?? (style.Dim ? color : p.SecondaryText);
                        if (style.Dim && style.IconColor is { } own)
                        {
                            iconColor = Palette.Blend(own, p.Background, 0.5);
                        }

                        TextRenderer.DrawText(g, icon, _iconFont, new Rectangle(x + textLeft, y, IconWidth, RowHeight), iconColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        textLeft += IconWidth + 4;
                    }

                    cell = new Rectangle(x + textLeft, y, width - textLeft - 6, RowHeight);
                }

                if (cell.Right > 0 && cell.Left < ClientSize.Width && cell.Width > 0)
                {
                    var text = _model.Text(id, c);
                    if (text.Length > 0)
                    {
                        var flags = column.Align == HorizontalAlignment.Right ? flagsRight : flagsLeft;
                        if (_model.IsChangedCell(id, c))
                        {
                            // The value that differs between the snapshots, on a highlight.
                            var size = TextRenderer.MeasureText(g, text, font, new Size(cell.Width, RowHeight), flags);
                            var w = Math.Min(cell.Width, size.Width) + 8;
                            var left = column.Align == HorizontalAlignment.Right ? cell.Right - w + 4 : cell.Left - 4;
                            g.FillRectangle(chip, left, y + 3, w, RowHeight - 6);
                        }

                        // New, modified and deleted colours go through every column (F27).
                        TextRenderer.DrawText(g, text, font, cell, color, flags);
                    }
                }

                x += width;
            }
        }

        if (!ShowHeader)
        {
            return;
        }

        // Header.
        using (var headerBrush = new SolidBrush(p.Background))
        {
            g.FillRectangle(headerBrush, 0, 0, ClientSize.Width, HeaderHeight);
        }

        using var border = new Pen(p.Border);
        g.DrawLine(border, 0, HeaderHeight - 1, ClientSize.Width, HeaderHeight - 1);
        var hx = -_left;
        for (var c = 0; c < Columns.Count; c++)
        {
            var column = Columns[c];
            var width = ColumnWidth(c);
            var rect = new Rectangle(hx + 6, 0, width - 12, HeaderHeight);
            var sorted = c == SortColumn;
            var right = column.Align == HorizontalAlignment.Right;
            if (sorted)
            {
                // The arrow goes on the side away from the text, as in the snapshot list.
                const int arrow = 14;
                var box = right ? new Rectangle(rect.Left, 0, arrow, HeaderHeight) : new Rectangle(rect.Right - arrow, 0, arrow, HeaderHeight);
                rect = right ? Rectangle.FromLTRB(rect.Left + arrow, 0, rect.Right, HeaderHeight) : Rectangle.FromLTRB(rect.Left, 0, rect.Right - arrow, HeaderHeight);
                DrawSortArrow(g, box, SortDescending, p.SecondaryText);
            }

            TextRenderer.DrawText(g, column.Header, sorted ? UiFonts.Bold : Font, rect, sorted ? p.Text : p.SecondaryText, right ? flagsRight : flagsLeft);
            hx += width;
            if (c < Columns.Count - 1 || !StretchLastColumn)
            {
                g.DrawLine(border, hx - 1, 5, hx - 1, HeaderHeight - 6);
            }
        }
    }

    /// <summary>Dotted lines for one row: through-lines of its ancestors, then its own elbow to the icon.</summary>
    private void DrawTreeLines(Graphics g, Pen pen, int left, int y, int row, bool hasChildren)
    {
        var depth = _depth[row];
        var mask = row < _lines.Count ? _lines[row] : 0;
        var middle = y + (RowHeight / 2);
        for (var k = 0; k < depth && k < 64; k++)
        {
            if ((mask & (1UL << k)) != 0)
            {
                var lx = left + (k * Indent) + (Indent / 2);
                g.DrawLine(pen, lx, y, lx, y + RowHeight);
            }
        }

        var cx = left + (depth * Indent) + (Indent / 2);
        var hasNext = depth < 64 && (mask & (1UL << depth)) != 0;
        if (row > 0 || depth > 0)
        {
            g.DrawLine(pen, cx, y, cx, middle);
        }

        if (hasNext)
        {
            g.DrawLine(pen, cx, middle, cx, y + RowHeight);
        }

        g.DrawLine(pen, hasChildren ? cx + 5 : cx, middle, cx + (Indent / 2) + 2, middle);
    }

    /// <summary>The + / − box of a row with children, centred on (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    private static void DrawBox(Graphics g, Pen border, Brush fill, Pen sign, int cx, int cy, bool expanded)
    {
        var box = new Rectangle(cx - 4, cy - 4, 8, 8);
        g.FillRectangle(fill, box);
        g.DrawRectangle(border, box);
        g.DrawLine(sign, cx - 2, cy, cx + 2, cy);
        if (!expanded)
        {
            g.DrawLine(sign, cx, cy - 2, cx, cy + 2);
        }
    }

    private static void DrawSortArrow(Graphics g, Rectangle box, bool descending, Color color)
    {
        var cx = box.Left + (box.Width / 2f);
        var cy = box.Top + (box.Height / 2f);
        PointF[] points = descending
            ? [new(cx - 4, cy - 2), new(cx + 4, cy - 2), new(cx, cy + 2.5f)]
            : [new(cx - 4, cy + 2), new(cx + 4, cy + 2), new(cx, cy - 2.5f)];
        var mode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, points);
        g.SmoothingMode = mode;
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
