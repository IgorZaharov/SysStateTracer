using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// The left pane of the comparison window: the three categories, then drives, folders, hives and keys, with no
/// files or values (those are in the list on the right). A category's id is -1/-2/-3; a folder's or key's id holds
/// bit 62, the area in bits 56–57 and its index + 1 in the older (bits 28–55) and newer (bits 0–27) snapshot,
/// so the same folder has the same id after Swap and after comparing again.
/// </summary>
internal sealed class ComparisonTreeModel(ComparisonData data) : ITreeGridModel
{
    private const long ContainerFlag = 1L << 62;
    private const int IndexBits = 28;
    private const long IndexMask = (1L << IndexBits) - 1;

    public ComparisonData Data { get; } = data;

    public IReadOnlyList<long> Roots { get; } = [CategoryId(DiffArea.FileSystem), CategoryId(DiffArea.Registry), CategoryId(DiffArea.Services), CategoryId(DiffArea.Environment)];

    public static long CategoryId(DiffArea area) => -(long)area - 1;

    public static long IdOf(ViewItem item) => item.IsCategory
        ? CategoryId(item.Area)
        : ContainerFlag | ((long)item.Area << 56) | ((long)(item.Older + 1) << IndexBits) | (uint)(item.Newer + 1);

    /// <summary>The folder, key or category of a row.</summary>
    public ViewItem ItemOf(long id) => id < 0
        ? ComparisonData.Category((DiffArea)(-id - 1))
        : Data.Container((DiffArea)((id >> 56) & 3), (int)((id >> IndexBits) & IndexMask) - 1, (int)(id & IndexMask) - 1);

    public bool HasChildren(long id) => Data.TreeHasChildren(ItemOf(id));

    public IReadOnlyList<long> Children(long id) =>
        [.. Data.TreeChildren(ItemOf(id)).Where(i => i.Older + 1 <= IndexMask && i.Newer + 1 <= IndexMask).Select(IdOf)];

    public string Text(long id, int column)
    {
        var item = ItemOf(id);
        return item.IsCategory ? Data.CategoryText(item.Area) : Data.DisplayName(item) + Data.CountText(item);
    }

    /// <summary>
    /// Drawn: the name, then the counts as badges, "+20 ~1 −1" in the change colours after a category and
    /// "6" or "14 items" in secondary text after a folder or key. <see cref="Text"/> keeps the words for tests.
    /// </summary>
    public (string Name, IReadOnlyList<GridBadge> Badges) Label(long id, int column)
    {
        var item = ItemOf(id);
        if (item.IsCategory)
        {
            var counts = Data.Counts(item.Area);
            var p = ThemeManager.Current;
            List<GridBadge> badges = [];
            if (counts.New > 0)
            {
                badges.Add(new GridBadge(string.Create(CultureInfo.CurrentCulture, $"+{counts.New:N0}"), p.New));
            }

            if (counts.Modified > 0)
            {
                badges.Add(new GridBadge(string.Create(CultureInfo.CurrentCulture, $"~{counts.Modified:N0}"), p.Modified));
            }

            if (counts.Deleted > 0)
            {
                badges.Add(new GridBadge(string.Create(CultureInfo.CurrentCulture, $"−{counts.Deleted:N0}"), p.Deleted));
            }

            if (badges.Count == 0)
            {
                badges.Add(new GridBadge(Data.NotInBoth(item.Area) ? "not in both snapshots" : "no changes"));
            }

            return (ComparisonData.CategoryName(item.Area), badges);
        }

        var count = Data.CountText(item).Trim().TrimStart('(').TrimEnd(')');
        return (Data.DisplayName(item), count.Length > 0 ? [new GridBadge(count)] : []);
    }

    public string? ToolTip(long id, int column) => Data.Path(ItemOf(id));

    public GridRowStyle Style(long id)
    {
        var item = ItemOf(id);
        if (item.IsCategory)
        {
            return new GridRowStyle(null, Strikeout: false, Dim: false, Bold: true);
        }

        var marked = Data.IsMarked(item);
        var icon = item.Area == DiffArea.FileSystem && Data.Path(item).Length <= 3 ? Glyphs.Drive : Glyphs.Folder;
        return new GridRowStyle(ComparisonData.TextColor(item.Status), Strikeout: marked, Dim: marked, Bold: false, icon, ComparisonData.IconColor(item.Status));
    }

    /// <summary>A name for a row that stays the same when the snapshots are compared again: the area and the path.</summary>
    public string RowKey(long id)
    {
        var item = ItemOf(id);
        return item.IsCategory
            ? "#" + ((int)item.Area).ToString(CultureInfo.InvariantCulture)
            : $"{(int)item.Area}|{Data.Path(item)}";
    }
}
