using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// Rows of the comparison window (plan §10.4): the three categories, then the comparison nodes. Row ids are
/// -1/-2/-3 for the categories and (area + 1) &lt;&lt; 32 | node for nodes, so they survive a swap.
/// Columns: Name · status glyph · snapshot 1 · snapshot 2. Text is made only for rows on screen.
/// A whole new or deleted folder or registry key is one comparison node; expanding it lists what it holds
/// straight from the snapshot that has it (F10, F11). Those "inside" rows have bit 62 set, bit 61 for a
/// registry value, bit 60 when they come from the newer snapshot, bit 59 for the file system, and the
/// entry, key or value index in the low 32 bits.
/// </summary>
internal sealed class ComparisonModel(DiffResult diff, ImportanceMarks marks, string? currentUserSid) : ITreeGridModel
{
    public const int NameColumn = 0;
    public const int StatusColumn = 1;
    public const int LeftColumn = 2;
    public const int RightColumn = 3;

    private const long InsideFlag = 1L << 62;
    private const long ValueFlag = 1L << 61;
    private const long NewerFlag = 1L << 60;
    private const long FileSystemFlag = 1L << 59;

    private static readonly DiffArea[] Areas = [DiffArea.FileSystem, DiffArea.Registry, DiffArea.Services];

    /// <summary>For each inside row listed so far: the whole new or deleted folder or key node it belongs to.</summary>
    private readonly Dictionary<long, int> _owner = [];

    /// <summary>For each inside registry value row: the key that holds it.</summary>
    private readonly Dictionary<long, int> _valueKey = [];

    public DiffResult Diff { get; } = diff;

    public IReadOnlyList<long> Roots { get; } = [CategoryId(DiffArea.FileSystem), CategoryId(DiffArea.Registry), CategoryId(DiffArea.Services)];

    public static long CategoryId(DiffArea area) => -(long)area - 1;

    public static long NodeId(DiffArea area, int node) => ((long)area + 1) << 32 | (uint)node;

    /// <summary>True for a row listed inside a whole new or deleted folder or key.</summary>
    public static bool IsInside(long id) => id > 0 && (id & InsideFlag) != 0;

    /// <summary>The area of an inside row.</summary>
    public static DiffArea InsideArea(long id) => (id & FileSystemFlag) != 0 ? DiffArea.FileSystem : DiffArea.Registry;

    private static long InsideId(DiffArea area, bool value, bool newer, int index) =>
        InsideFlag | (area == DiffArea.FileSystem ? FileSystemFlag : 0) | (value ? ValueFlag : 0) | (newer ? NewerFlag : 0) | (uint)index;

    private static bool IsValue(long id) => (id & ValueFlag) != 0;

    private static int Index(long id) => (int)(id & 0xFFFFFFFF);

    private Snapshot SideOf(long id) => (id & NewerFlag) != 0 ? Diff.Newer : Diff.Older;

    /// <summary>
    /// What Jump to opens for an inside row: the file or folder itself in Explorer, or in Regedit the key, or
    /// the key holding the value.
    /// </summary>
    public string InsidePath(long id)
    {
        var snapshot = SideOf(id);
        if (InsideArea(id) == DiffArea.FileSystem)
        {
            return snapshot.FileSystem.GetFullPath(Index(id), snapshot.Names);
        }

        var key = IsValue(id) ? _valueKey[id] : Index(id);
        return snapshot.Registry.GetFullPath(key, snapshot.Names);
    }

    /// <summary>The comparison node (a whole new or deleted folder or key) an inside row belongs to.</summary>
    public DiffItem? OwnerOf(long id) => _owner.TryGetValue(id, out var node) ? new DiffItem(InsideArea(id), node) : null;

    /// <summary>A folder or key node that is new or deleted as a whole, and holds something to list.</summary>
    private bool IsWhole(DiffItem item, out bool newer, out int index)
    {
        newer = false;
        index = -1;
        if (item.Area == DiffArea.Services || DiffActions.IsCategory(item))
        {
            return false;
        }

        var tree = Diff.Tree(item.Area);
        var container = item.Area == DiffArea.FileSystem ? DiffItemKind.Folder : DiffItemKind.Key;
        if (tree.Kind(item.Node) != container || tree.Change(item.Node) is not (ChangeKind.New or ChangeKind.Deleted)
            || tree.Descendants(item.Node) > 0)
        {
            return false;
        }

        var right = tree.Right(item.Node) >= 0;
        index = right ? tree.Right(item.Node) : tree.Left(item.Node);
        newer = (right ? Diff.Right : Diff.Left) == Diff.Newer;
        return HoldsAnything(item.Area, newer ? Diff.Newer : Diff.Older, index);
    }

    private static bool HoldsAnything(DiffArea area, Snapshot snapshot, int index) => area == DiffArea.FileSystem
        ? snapshot.FileSystem.Descendants[index] > 0
        : snapshot.Registry.Descendants[index] > 0 || snapshot.Registry.KeyValueCount[index] > 0;

    private List<long> InsideChildren(DiffArea area, bool newer, int index, int owner)
    {
        var snapshot = newer ? Diff.Newer : Diff.Older;
        var names = snapshot.Names;
        var checkIgnore = Diff.Ignore.RuleCount > 0;
        var children = new List<long>();
        if (area == DiffArea.FileSystem)
        {
            // Folders first, then files, as Explorer shows them.
            var fs = snapshot.FileSystem;
            var target = IgnoreTarget.FileSystem;
            var path = checkIgnore ? fs.GetFullPath(index, names) : string.Empty;
            var files = new List<long>();
            for (var c = index + 1; c <= index + fs.Descendants[index]; c += fs.Descendants[c] + 1)
            {
                if (!checkIgnore || !Diff.Ignore.IsIgnoredEntry(target, path + "\\" + names.GetString(fs.Name[c])))
                {
                    (fs.IsDirectory(c) ? children : files).Add(InsideId(area, value: false, newer, c));
                }
            }

            children.AddRange(files);
        }
        else
        {
            var reg = snapshot.Registry;
            var target = IgnoreTarget.Registry;
            var path = checkIgnore ? reg.GetFullPath(index, names) : string.Empty;
            for (var c = index + 1; c <= index + reg.Descendants[index]; c += reg.Descendants[c] + 1)
            {
                if (!checkIgnore || !Diff.Ignore.IsIgnoredEntry(target, path + "\\" + names.GetString(reg.Name[c])))
                {
                    children.Add(InsideId(area, value: false, newer, c));
                }
            }

            for (var v = reg.FirstValue[index]; v < reg.FirstValue[index] + reg.KeyValueCount[index]; v++)
            {
                if (!checkIgnore || !Diff.Ignore.IsIgnoredEntry(target, path + "\\" + names.GetString(reg.ValueName[v])))
                {
                    var id = InsideId(area, value: true, newer, v);
                    _valueKey[id] = index;
                    children.Add(id);
                }
            }
        }

        foreach (var child in children)
        {
            _owner[child] = owner;
        }

        return children;
    }

    public bool HasChildren(long id)
    {
        if (IsInside(id))
        {
            return !IsValue(id) && HoldsAnything(InsideArea(id), SideOf(id), Index(id));
        }

        var item = Item(id);
        if (IsWhole(item, out _, out _))
        {
            return true;
        }

        var tree = Diff.Tree(item.Area);
        return DiffActions.IsCategory(item) ? tree.Count > 0 : tree.Descendants(item.Node) > 0;
    }

    public IReadOnlyList<long> Children(long id)
    {
        if (IsInside(id))
        {
            return IsValue(id) || !_owner.TryGetValue(id, out var owner)
                ? []
                : InsideChildren(InsideArea(id), (id & NewerFlag) != 0, Index(id), owner);
        }

        var item = Item(id);
        if (IsWhole(item, out var newer, out var index))
        {
            return InsideChildren(item.Area, newer, index, item.Node);
        }

        var tree = Diff.Tree(item.Area);
        var children = DiffActions.IsCategory(item) ? tree.Roots() : tree.Children(item.Node);
        return [.. children.Select(n => NodeId(item.Area, n))];
    }

    public string Text(long id, int column)
    {
        if (IsInside(id))
        {
            return InsideText(id, column);
        }

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
        if (IsInside(id))
        {
            return InsideToolTip(id, column);
        }

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
        if (IsInside(id))
        {
            var owner = OwnerOf(id);
            return owner is { } o ? Style(NodeId(o.Area, o.Node)) : new GridRowStyle(null, Strikeout: false, Dim: false, Bold: false);
        }

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

    private string InsideText(long id, int column)
    {
        var snapshot = SideOf(id);
        var index = Index(id);
        var names = snapshot.Names;
        if (column == StatusColumn)
        {
            return snapshot == Diff.Newer ? "+" : "−";
        }

        if (InsideArea(id) == DiffArea.FileSystem)
        {
            var fs = snapshot.FileSystem;
            if (column == NameColumn)
            {
                var name = names.GetString(fs.Name[index]);
                var items = fs.Descendants[index];
                return items > 0 ? string.Create(CultureInfo.CurrentCulture, $"{name}  ({items:N0} items)") : name;
            }

            if ((column == RightColumn) != (snapshot == Diff.Right))
            {
                return string.Empty;
            }

            return fs.IsDirectory(index)
                ? string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Time(fs.LastWriteTime[index])} · {DiffItems.Count(fs.ChildFolders[index], "folder")}, {DiffItems.Count(fs.ChildFiles[index], "file")}")
                : $"{ItemFormat.Time(fs.LastWriteTime[index])} · {ItemFormat.Size(fs.Size[index])}";
        }

        var reg = snapshot.Registry;
        if (column == NameColumn)
        {
            if (IsValue(id))
            {
                return ValueName(snapshot, index);
            }

            var name = names.GetString(reg.Name[index]);
            var items = reg.Descendants[index] + CountValues(reg, index);
            return items > 0 ? string.Create(CultureInfo.CurrentCulture, $"{name}  ({items:N0} items)") : name;
        }

        if ((column == RightColumn) != (snapshot == Diff.Right))
        {
            return string.Empty;
        }

        if (IsValue(id))
        {
            return $"{ItemFormat.ValueType(reg.ValueDataType[index])} · {ItemFormat.ValueData(reg, index)}";
        }

        var keys = 0;
        for (var c = index + 1; c <= index + reg.Descendants[index]; c += reg.Descendants[c] + 1)
        {
            keys++;
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{ItemFormat.Time(reg.LastWriteTime[index])} · {DiffItems.Count(keys, "key")}, {DiffItems.Count(reg.KeyValueCount[index], "value")}");
    }

    private string? InsideToolTip(long id, int column)
    {
        var snapshot = SideOf(id);
        var index = Index(id);
        if (column == NameColumn)
        {
            return InsideArea(id) == DiffArea.Registry && IsValue(id)
                ? InsidePath(id) + "\\" + ValueName(snapshot, index)
                : InsidePath(id);
        }

        if (column is not (LeftColumn or RightColumn) || InsideText(id, column).Length == 0)
        {
            return null;
        }

        if (InsideArea(id) == DiffArea.FileSystem)
        {
            var fs = snapshot.FileSystem;
            return fs.IsDirectory(index)
                ? InsideText(id, column)
                : string.Create(CultureInfo.CurrentCulture, $"{fs.Size[index]:N0} bytes · attributes {ItemFormat.Attributes(fs.Attributes[index])}");
        }

        return IsValue(id) ? Shorten(ItemFormat.ValueData(snapshot.Registry, index, full: true)) : InsideText(id, column);
    }

    private static string ValueName(Snapshot snapshot, int value)
    {
        var name = snapshot.Names.GetString(snapshot.Registry.ValueName[value]);
        return name.Length == 0 ? "(Default)" : name;
    }

    /// <summary>Values in the key and everything below it.</summary>
    private static long CountValues(RegTree reg, int key)
    {
        long total = 0;
        for (var k = key; k <= key + reg.Descendants[key]; k++)
        {
            total += reg.KeyValueCount[k];
        }

        return total;
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
