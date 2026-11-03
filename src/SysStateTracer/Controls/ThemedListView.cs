using System.Runtime.InteropServices;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>
/// A details list drawn in the palette: header in secondary text, selection in the accent tint, an optional
/// drop-target row (W1 drag-and-drop compare). The last column fills the width so the header has no bare strip.
/// A whole row, every column, is drawn in <see cref="OnDrawItem"/>: the list sometimes repaints a row without
/// asking for its other columns (with the mouse over it or between rows), which left them blank (F15).
/// Grid lines and zebra stripes follow <see cref="ThemeManager.GridLines"/> and <see cref="ThemeManager.ZebraStripes"/>.
/// </summary>
internal sealed class ThemedListView : ListView, IThemed
{
    private Palette _palette = ThemeManager.Current;
    private int _dropTarget = -1;
    private int _hover = -1;
    private ImageList? _rowHeight;

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
        if (m.Msg == 0x204E /* WM_REFLECT | WM_NOTIFY */ && CustomDrawStage(m.LParam) is { } draw)
        {
            base.WndProc(ref m);
            if (draw.DrawStage == 0x10001 /* CDDS_ITEMPREPAINT */)
            {
                m.Result |= 0x10; // CDRF_NOTIFYPOSTPAINT
            }
            else if (draw.DrawStage == 0x10002 /* CDDS_ITEMPOSTPAINT */)
            {
                RepaintRow(draw);
            }

            return;
        }

        base.WndProc(ref m);
        if (m.Msg == 0x000F /* WM_PAINT */)
        {
            PaintEmptyArea();
        }
    }

    /// <summary>The list's own custom-draw notification for a row, or null for any other notification.</summary>
    private NmCustomDraw? CustomDrawStage(nint lParam)
    {
        var header = Marshal.PtrToStructure<NmHdr>(lParam);
        return header.Code == -12 /* NM_CUSTOMDRAW */ && header.HwndFrom == Handle
            ? Marshal.PtrToStructure<NmCustomDraw>(lParam)
            : null;
    }

    /// <summary>
    /// The Explorer list theme draws its own translucent selection over the selected row after <see cref="OnDrawItem"/>,
    /// whatever the custom-draw flags say. The row is drawn again once Windows is done with it, so only the
    /// Windows 11 rounded selection shows.
    /// </summary>
    private void RepaintRow(NmCustomDraw draw)
    {
        var index = (int)draw.ItemSpec;
        if (index < 0 || index >= Items.Count || draw.Hdc == 0)
        {
            return;
        }

        var item = Items[index];
        if (!item.Selected && index != _dropTarget)
        {
            return;
        }

        using var g = Graphics.FromHdc(draw.Hdc);
        DrawRow(g, item, index, GetItemRect(index, ItemBoundsPortion.Entire));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmHdr
    {
        public nint HwndFrom;
        public nuint IdFrom;
        public int Code;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NmCustomDraw
    {
        public NmHdr Header;
        public uint DrawStage;
        public nint Hdc;
        public Rectangle Rect;
        public nuint ItemSpec;
        public uint ItemState;
        public nint ItemParam;
    }

    /// <summary>The list's theme draws column lines below the last row; that area is cleared to the background.</summary>
    private void PaintEmptyArea()
    {
        // Between BeginUpdate and EndUpdate, Items holds rows the native list has not got yet: ask the list itself.
        var count = NativeItemCount();
        if (count != Items.Count)
        {
            return;
        }

        var top = HeaderBottom();
        if (count > 0)
        {
            var last = GetItemRect(count - 1);
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

    private int NativeItemCount()
    {
        var message = Message.Create(Handle, 0x1004 /* LVM_GETITEMCOUNT */, 0, 0);
        DefWndProc(ref message);
        return (int)message.Result;
    }

    private int HeaderBottom() => Items.Count > 0 ? GetItemRect(0).Top : (HeaderStyle == ColumnHeaderStyle.None ? 0 : Font.Height + 10);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetExplorerTheme(Handle, _palette.IsDark);
        SetRowHeight();
        FillLastColumn();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        SetRowHeight();
    }

    /// <summary>Space above and below the text of each row at 96 DPI: 16 is Windows 11's standard row, 8 its compact one.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RowPadding
    {
        get => _rowPadding;
        set
        {
            _rowPadding = value;
            SetRowHeight();
        }
    }

    private int _rowPadding = 16;

    /// <summary>Windows 11 row height (the text plus <see cref="RowPadding"/>): a details list takes it from its small image list.</summary>
    private void SetRowHeight()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var old = _rowHeight;
        _rowHeight = new ImageList { ImageSize = new Size(1, Math.Min(256, Font.Height + LogicalToDeviceUnits(_rowPadding))) };
        SmallImageList = _rowHeight;
        old?.Dispose();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(GetItemAt(e.X, e.Y)?.Index ?? -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == _hover)
        {
            return;
        }

        var old = _hover;
        _hover = index;
        InvalidateRow(old);
        InvalidateRow(index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rowHeight?.Dispose();
        }

        base.Dispose(disposing);
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

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => DrawRow(e.Graphics, e.Item, e.ItemIndex, e.Bounds);

    private void DrawRow(Graphics g, ListViewItem row, int index, Rectangle bounds)
    {
        var p = _palette;
        var back = ThemeManager.ZebraStripes && index % 2 == 1 ? p.Stripe : p.Background;
        using (var brush = new SolidBrush(back))
        {
            g.FillRectangle(brush, bounds);
        }

        // Windows 11 list rows: a rounded fill inset from the edges, and an accent bar on selected rows.
        Color? fill = index == _dropTarget ? p.DropTarget
            : row.Selected ? (Focused ? p.Selection : p.SelectionInactive)
            : index == _hover ? p.Hover
            : null;
        if (fill is { } f)
        {
            var inset = LogicalToDeviceUnits(4);
            var item = new Rectangle(bounds.Left + inset, bounds.Top + 1, bounds.Width - (2 * inset) - 1, bounds.Height - 3);
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = ThemedButton.Rounded(item, 4))
            using (var brush = new SolidBrush(f))
            {
                g.FillPath(brush, path);
            }

            if (row.Selected)
            {
                var bar = LogicalToDeviceUnits(16);
                using var pill = ThemedButton.Rounded(new Rectangle(item.Left, bounds.Top + ((bounds.Height - bar) / 2), LogicalToDeviceUnits(3), bar), 1);
                using var accent = new SolidBrush(p.Accent);
                g.FillPath(accent, pill);
            }

            g.SmoothingMode = smoothing;
        }

        var x = bounds.Left;
        for (var column = 0; column < Columns.Count; column++)
        {
            var width = Columns[column].Width;
            if (column < row.SubItems.Count)
            {
                var color = ItemColor?.Invoke(row, column) ?? _palette.Text;
                var cell = Rectangle.Inflate(new Rectangle(x, bounds.Top, width, bounds.Height), -6, 0);
                if (column == 0)
                {
                    // Room for the selection bar before the first column's text.
                    cell = Rectangle.FromLTRB(cell.Left + LogicalToDeviceUnits(8), cell.Top, cell.Right, cell.Bottom);
                }

                var align = Columns[column].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(g, row.SubItems[column].Text, Font, cell, color, align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            x += width;
        }

        if (ThemeManager.GridLines)
        {
            using var pen = new Pen(p.GridLine);
            var bottom = bounds.Bottom - 1;
            g.DrawLine(pen, bounds.Left, bottom, bounds.Right, bottom);
            var line = bounds.Left;
            for (var column = 0; column < Columns.Count - 1; column++)
            {
                line += Columns[column].Width;
                g.DrawLine(pen, line - 1, bounds.Top, line - 1, bottom);
            }
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
