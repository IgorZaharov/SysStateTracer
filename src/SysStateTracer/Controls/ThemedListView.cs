using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A details list drawn in the palette: header in secondary text, selection in the accent tint, an optional
/// drop-target row (W1 drag-and-drop compare). The last column fills the width so the header has no bare strip.
/// A whole row, every column, is drawn in <see cref="OnDrawItem"/>: the list sometimes repaints a row without
/// asking for its other columns (with the mouse over it or between rows), which left them blank (F15).
/// </summary>
internal sealed class ThemedListView : ListView, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private int _dropTarget = -1;

    public ThemedListView()
    {
        View = View.Details;
        FullRowSelect = true;
        OwnerDraw = true;
        DoubleBuffered = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Font = UiFonts.Normal;
    }

    /// <summary>Column with the sort arrow in its header, or -1.</summary>
    public int SortColumn { get; set; } = -1;

    /// <summary>Arrow down (descending) or up.</summary>
    public bool SortDescending { get; set; }

    /// <summary>Text colour for an item; null = normal text.</summary>
    public Func<ListViewItem, int, Color?>? ItemColor { get; set; }

    /// <summary>Row highlighted as a drop target, or -1.</summary>
    public int DropTargetIndex
    {
        get => _dropTarget;
        set
        {
            if (_dropTarget == value)
            {
                return;
            }

            var old = _dropTarget;
            _dropTarget = value;
            InvalidateRow(old);
            InvalidateRow(value);
        }
    }

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        ForeColor = palette.Text;
        if (IsHandleCreated)
        {
            NativeUi.SetExplorerTheme(Handle, palette.IsDark);
        }

        Invalidate();
    }

    /// <summary>Keeps the last column as wide as the remaining space.</summary>
    public void FillLastColumn()
    {
        if (Columns.Count == 0 || !IsHandleCreated)
        {
            return;
        }

        var others = 0;
        for (var i = 0; i < Columns.Count - 1; i++)
        {
            others += Columns[i].Width;
        }

        Columns[^1].Width = Math.Max(60, ClientSize.Width - others);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F /* WM_PAINT */)
        {
            PaintEmptyArea();
        }
    }

    /// <summary>The list's theme draws column lines below the last row; that area is cleared to the background.</summary>
    private void PaintEmptyArea()
    {
        var top = HeaderBottom();
        if (Items.Count > 0)
        {
            var last = GetItemRect(Items.Count - 1);
            top = Math.Max(top, last.Bottom);
        }

        if (top >= ClientSize.Height)
        {
            return;
        }

        using var g = CreateGraphics();
        using var brush = new SolidBrush(_palette.Background);
        g.FillRectangle(brush, 0, top, ClientSize.Width, ClientSize.Height - top);
    }

    private int HeaderBottom() => Items.Count > 0 ? GetItemRect(0).Top : (HeaderStyle == ColumnHeaderStyle.None ? 0 : Font.Height + 10);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetExplorerTheme(Handle, _palette.IsDark);
        FillLastColumn();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FillLastColumn();
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (e.ColumnIndex < Columns.Count - 1)
        {
            BeginInvoke(FillLastColumn);
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var p = _palette;
        using (var brush = new SolidBrush(p.Background))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        using (var pen = new Pen(p.Border))
        {
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (e.ColumnIndex < Columns.Count - 1)
            {
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
        }

        var text = Rectangle.Inflate(e.Bounds, -6, 0);
        var right = e.Header?.TextAlign == HorizontalAlignment.Right;
        var align = right ? TextFormatFlags.Right : TextFormatFlags.Left;
        var sorted = e.ColumnIndex == SortColumn;
        if (sorted)
        {
            // The arrow goes on the side away from the text: right of left-aligned headers, left of right-aligned ones.
            const int arrow = 14;
            var arrowBox = right
                ? new Rectangle(text.Left, e.Bounds.Top, arrow, e.Bounds.Height)
                : new Rectangle(text.Right - arrow, e.Bounds.Top, arrow, e.Bounds.Height);
            text = right
                ? Rectangle.FromLTRB(text.Left + arrow, text.Top, text.Right, text.Bottom)
                : Rectangle.FromLTRB(text.Left, text.Top, text.Right - arrow, text.Bottom);
            DrawSortArrow(e.Graphics, arrowBox, SortDescending, p.SecondaryText);
        }

        TextRenderer.DrawText(e.Graphics, e.Header?.Text, sorted ? UiFonts.Bold : Font, text, sorted ? p.Text : p.SecondaryText, align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
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

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var p = _palette;
        var back = e.ItemIndex == _dropTarget ? p.DropTarget
            : e.Item.Selected ? (Focused ? p.Selection : p.SelectionInactive)
            : p.Background;
        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        var x = e.Bounds.Left;
        for (var column = 0; column < Columns.Count; column++)
        {
            var width = Columns[column].Width;
            if (column < e.Item.SubItems.Count)
            {
                var color = ItemColor?.Invoke(e.Item, column) ?? _palette.Text;
                var bounds = Rectangle.Inflate(new Rectangle(x, e.Bounds.Top, width, e.Bounds.Height), -6, 0);
                var align = Columns[column].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(e.Graphics, e.Item.SubItems[column].Text, Font, bounds, color, align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            x += width;
        }
    }

    /// <summary>Everything is drawn with the row in <see cref="OnDrawItem"/>.</summary>
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
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

    private void InvalidateRow(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Invalidate(GetItemRect(index));
        }
    }
}
