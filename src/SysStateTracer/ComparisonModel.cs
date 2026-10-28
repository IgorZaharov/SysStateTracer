using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// Rows of the comparison window (plan §10.4): the three categories, then the comparison nodes. Row ids are
/// -1/-2/-3 for the categories and (area + 1) &lt;&lt; 32 | node for nodes, so they survive a swap.
/// Columns: Name · status glyph · snapshot 1 · snapshot 2. Text is made only for rows on screen.
/// </summary>
internal sealed class ComparisonModel(DiffResult diff, ImportanceMarks marks, string? currentUserSid) : ITreeGridModel
{
    public const int NameColumn = 0;
    public const int StatusColumn = 1;
    public const int LeftColumn = 2;
    public const int RightColumn = 3;

    private static readonly DiffArea[] Areas = [DiffArea.FileSystem, DiffArea.Registry, DiffArea.Services];

    public DiffResult Diff { get; } = diff;

    public IReadOnlyList<long> Roots { get; } = [CategoryId(DiffArea.FileSystem), CategoryId(DiffArea.Registry), CategoryId(DiffArea.Services)];

    public static long CategoryId(DiffArea area) => -(long)area - 1;

    public static long NodeId(DiffArea area, int node) => ((long)area + 1) << 32 | (uint)node;

    /// <summary>The comparison item of a row; categories have <c>Node = -1</c>.</summary>
    public static DiffItem Item(long id) => id < 0
        ? DiffActions.Category((DiffArea)(-id - 1))
        : new DiffItem((DiffArea)((id >> 32) - 1), (int)(id & 0xFFFFFFFF));

    public bool HasChildren(long id)
    {
        var item = Item(id);
        var tree = Diff.Tree(item.Area);
        return DiffActions.IsCategory(item) ? tree.Count > 0 : tree.Descendants(item.Node) > 0;
    }

    public IReadOnlyList<long> Children(long id)
    {
        var item = Item(id);
        var tree = Diff.Tree(item.Area);
        var children = DiffActions.IsCategory(item) ? tree.Roots() : tree.Children(item.Node);
        return [.. children.Select(n => NodeId(item.Area, n))];
    }

    public string Text(long id, int column)
    {
        var item = Item(id);
        if (DiffActions.IsCategory(item))
        {
            return column == NameColumn ? CategoryText(item.Area) : string.Empty;
        }

        var tree = Diff.Tree(item.Area);
        return column switch
        {
            NameColumn => NameText(item, tree),
            StatusColumn => tree.Change(item.Node) switch
            {
                ChangeKind.New => "+",
                ChangeKind.Deleted => "−",
                ChangeKind.Modified => "~",
                _ => string.Empty,
            },
            LeftColumn => DiffItems.Column(Diff, item, right: false),
            _ => DiffItems.Column(Diff, item, right: true),
        };
    }

    public string? ToolTip(long id, int column)
    {
        var item = Item(id);
        if (DiffActions.IsCategory(item))
        {
            return null;
        }

        var tree = Diff.Tree(item.Area);
        if (column == NameColumn)
        {
            var path = Diff.FullPath(tree, item.Node);
            var change = tree.Change(item.Node);
            var count = tree.ChangedItems(item.Node);
            return change is ChangeKind.New or ChangeKind.Deleted && count > 1
                ? string.Create(CultureInfo.CurrentCulture, $"{path}{Environment.NewLine}{count:N0} items")
                : path;
        }

        if (column is LeftColumn or RightColumn)
        {
            var right = column == RightColumn;
            var snapshot = right ? Diff.Right : Diff.Left;
            var index = right ? tree.Right(item.Node) : tree.Left(item.Node);
            if (index < 0)
            {
                return null;
            }

            return tree.Kind(item.Node) switch
            {
                DiffItemKind.File => string.Create(CultureInfo.CurrentCulture, $"{snapshot.FileSystem.Size[index]:N0} bytes · attributes {ItemFormat.Attributes(snapshot.FileSystem.Attributes[index])}"),
                DiffItemKind.Value => Shorten(ItemFormat.ValueData(snapshot.Registry, index, full: true)),
                _ => DiffItems.Column(Diff, item, right),
            };
        }

        return null;
    }

    public GridRowStyle Style(long id)
    {
        var item = Item(id);
        if (DiffActions.IsCategory(item))
        {
            return new GridRowStyle(null, Strikeout: false, Dim: false, Bold: true);
        }

        var p = ThemeManager.Current;
        var color = Diff.Tree(item.Area).Change(item.Node) switch
        {
            ChangeKind.New => p.New,
            ChangeKind.Modified => p.Modified,
            ChangeKind.Deleted => p.Deleted,
            _ => (Color?)null,
        };
        var marked = marks.IsMarkedOrInside(Diff, item);
        return new GridRowStyle(color, Strikeout: marked, Dim: marked, Bold: false);
    }

    private static string Shorten(string text) => text.Length <= 2000 ? text : text[..2000] + " …";

    private string CategoryText(DiffArea area)
    {
        var counts = area switch
        {
            DiffArea.FileSystem => Diff.FileSystemCounts,
            DiffArea.Registry => Diff.RegistryCounts,
            _ => Diff.ServicesCounts,
        };
        var name = area switch
        {
            DiffArea.FileSystem => "File system",
            DiffArea.Registry => "Registry",
            _ => "Services",
        };
        return counts.Total == 0
            ? $"{name}  (no changes)"
            : string.Create(CultureInfo.CurrentCulture, $"{name}  ({counts.New:N0} new, {counts.Modified:N0} modified, {counts.Deleted:N0} deleted)");
    }

    private string NameText(DiffItem item, DiffTree tree)
    {
        var node = item.Node;
        var name = Diff.Name(tree, node);
        var parent = tree.Parent(node);
        switch (tree.Kind(node))
        {
            case DiffItemKind.Folder when parent < 0:
                name += "\\";
                break;
            case DiffItemKind.Value when name.Length == 0:
                name = "(Default)";
                break;
            case DiffItemKind.Key when parent >= 0 && tree.Parent(parent) < 0 && Diff.Name(tree, parent) == "HKU"
                && string.Equals(name, currentUserSid, StringComparison.OrdinalIgnoreCase):
                name = $"HKCU  ({name})";
                break;
            case DiffItemKind.Service:
                var snapshot = tree.Left(node) >= 0 ? Diff.Left : Diff.Right;
                var index = tree.Left(node) >= 0 ? tree.Left(node) : tree.Right(node);
                var display = snapshot.Names.GetString(snapshot.Services.DisplayName[index]);
                name = string.IsNullOrEmpty(display) || display == name ? name : $"{display}  ({name})";
                break;
        }

        var change = tree.Change(node);
        var count = tree.ChangedItems(node);
        if (change == ChangeKind.Unchanged && tree.Descendants(node) > 0)
        {
            name += string.Create(CultureInfo.CurrentCulture, $"  ({count:N0})");
        }
        else if (change is ChangeKind.New or ChangeKind.Deleted && count > 1)
        {
            name += string.Create(CultureInfo.CurrentCulture, $"  ({count:N0} items)");
        }

        return name;
    }

    /// <summary>All rows for the selection, as comparison items.</summary>
    public static List<DiffItem> Items(IEnumerable<long> ids) => [.. ids.Select(Item)];

    /// <summary>Areas in display order.</summary>
    public static IReadOnlyList<DiffArea> AllAreas => Areas;
}
