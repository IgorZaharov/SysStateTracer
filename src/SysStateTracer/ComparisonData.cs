using System.Globalization;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>How an item in the comparison window differs.</summary>
internal enum RowStatus : byte
{
    /// <summary>The same in both snapshots.</summary>
    Same,

    /// <summary>Only in the right-hand snapshot.</summary>
    New,

    /// <summary>Only in the left-hand snapshot.</summary>
    Deleted,

    /// <summary>In both, with differences.</summary>
    Modified,

    /// <summary>A folder or key that is the same itself but holds changes (blue).</summary>
    Inside,
}

/// <summary>
/// One folder, file, key, value or service in the comparison window: its index in the older and newer
/// snapshot (-1 where it is not there), its comparison node (-1 when it is unchanged or lies inside a whole
/// new or deleted folder or key) and how it differs.
/// </summary>
internal readonly record struct ViewItem(DiffArea Area, int Older, int Newer, int Node, RowStatus Status, bool IsValue = false)
{
    /// <summary>A category row ("File system", "Registry", "Services").</summary>
    public bool IsCategory => Older < 0 && Newer < 0;
}

/// <summary>
/// What both panes of the comparison window show (F-requests 1–6 of 1.1.0): folders and keys on the left,
/// the files, values or services of the selected one on the right. Everything is found by its index in the
/// two snapshots, so it is the same after Swap or a new comparison. With <see cref="ShowAll"/> unchanged
/// folders, files, keys and values are listed too, read from the snapshots only for what is opened; without
/// it, only what changed and the folders and keys leading to it. The contents of a whole new or deleted
/// folder or key are read from the snapshot that has it.
/// </summary>
internal sealed class ComparisonData(DiffResult diff, ImportanceMarks marks, string? currentUserSid, bool showAll)
{
    private readonly Dictionary<(int Older, int Newer), int>?[] _containerNodes = new Dictionary<(int, int), int>?[3];
    private readonly Dictionary<(int Older, int Newer), int>?[] _leafNodes = new Dictionary<(int, int), int>?[3];

    public DiffResult Diff { get; } = diff;

    public ImportanceMarks Marks { get; } = marks;

    /// <summary>Unchanged items are listed too (Show all); otherwise only changes.</summary>
    public bool ShowAll { get; } = showAll;

    public static ViewItem Category(DiffArea area) => new(area, -1, -1, -1, RowStatus.Same);

    /// <summary>A folder or key from its indices; its comparison node and status are looked up.</summary>
    public ViewItem Container(DiffArea area, int older, int newer)
    {
        var node = ContainerNode(area, older, newer);
        return new ViewItem(area, older, newer, node, StatusOf(area, older, newer, node));
    }

    // ---- Lookups ----

    private int ContainerNode(DiffArea area, int older, int newer)
    {
        Index(area);
        return _containerNodes[(int)area]!.TryGetValue((older, newer), out var node) ? node : -1;
    }

    private int LeafNode(DiffArea area, int older, int newer)
    {
        Index(area);
        return _leafNodes[(int)area]!.TryGetValue((older, newer), out var node) ? node : -1;
    }

    private void Index(DiffArea area)
    {
        if (_containerNodes[(int)area] is not null)
        {
            return;
        }

        var containers = new Dictionary<(int, int), int>();
        var leaves = new Dictionary<(int, int), int>();
        var tree = Diff.Tree(area);
        for (var n = 0; n < tree.Count; n++)
        {
            var key = (tree.OlderIndex(n), tree.NewerIndex(n));
            (IsContainer(tree.Kind(n)) ? containers : leaves)[key] = n;
        }

        _containerNodes[(int)area] = containers;
        _leafNodes[(int)area] = leaves;
    }

    private static bool IsContainer(DiffItemKind kind) => kind is DiffItemKind.Folder or DiffItemKind.Key;

    /// <summary>How an item differs; "new" is always "only in the right-hand snapshot", so Swap turns + into −.</summary>
    public RowStatus StatusOf(DiffArea area, int older, int newer, int node)
    {
        if (node >= 0)
        {
            return Diff.Tree(area).Change(node) switch
            {
                ChangeKind.New => RowStatus.New,
                ChangeKind.Deleted => RowStatus.Deleted,
                ChangeKind.Modified => RowStatus.Modified,
                _ => RowStatus.Inside,
            };
        }

        if (older >= 0 && newer >= 0)
        {
            return RowStatus.Same;
        }

        // Only on one side: inside a whole new or deleted folder or key.
        var onRight = newer >= 0 ? ReferenceEquals(Diff.Right, Diff.Newer) : ReferenceEquals(Diff.Right, Diff.Older);
        return onRight ? RowStatus.New : RowStatus.Deleted;
    }

    /// <summary>A comparison node for a folder or key that is new or deleted as a whole (its contents come from the snapshot).</summary>
    private bool IsWhole(DiffArea area, int node)
    {
        if (node < 0)
        {
            return false;
        }

        var tree = Diff.Tree(area);
        return IsContainer(tree.Kind(node)) && tree.Descendants(node) == 0 && tree.Change(node) is ChangeKind.New or ChangeKind.Deleted;
    }

    /// <summary>The comparison item of a row, for the context-menu actions; null for one that is for information only.</summary>
    public static DiffItem? ItemOf(ViewItem item) =>
        item.IsCategory ? DiffActions.Category(item.Area) : item.Node >= 0 ? new DiffItem(item.Area, item.Node) : null;

    public bool IsMarked(ViewItem item) => item.Node >= 0 && Marks.IsMarkedOrInside(Diff, new DiffItem(item.Area, item.Node));

    // ---- Folders and keys (the tree) ----

    /// <summary>Drives or hives under a category.</summary>
    public List<ViewItem> CategoryChildren(DiffArea area)
    {
        if (area == DiffArea.Services)
        {
            return [];
        }

        if (!ShowAll)
        {
            var tree = Diff.Tree(area);
            return Sorted([.. tree.Roots().Where(n => IsContainer(tree.Kind(n))).Select(n => FromNode(area, n))]);
        }

        return area == DiffArea.FileSystem
            ? Merge(area, value: false, FsChildren(Diff.Older, -1, folders: true), FsChildren(Diff.Newer, -1, folders: true), string.Empty, container: true)
            : Merge(area, value: false, KeyChildren(Diff.Older, -1), KeyChildren(Diff.Newer, -1), string.Empty, container: true);
    }

    public bool CategoryHasChildren(DiffArea area) => area switch
    {
        DiffArea.Services => false,
        _ when !ShowAll => Diff.Tree(area).Count > 0,
        DiffArea.FileSystem => Diff.Older.FileSystem.Count + Diff.Newer.FileSystem.Count > 0,
        _ => Diff.Older.Registry.KeyCount + Diff.Newer.Registry.KeyCount > 0,
    };

    /// <summary>The folders or keys directly inside a folder or key, sorted by name.</summary>
    public List<ViewItem> ContainerChildren(ViewItem parent)
    {
        if (parent.IsCategory)
        {
            return CategoryChildren(parent.Area);
        }

        var area = parent.Area;
        if (!ShowAll && parent.Node >= 0 && !IsWhole(area, parent.Node))
        {
            var tree = Diff.Tree(area);
            return Sorted([.. tree.Children(parent.Node).Where(c => IsContainer(tree.Kind(c))).Select(c => FromNode(area, c))]);
        }

        if (!ShowAll && parent.Older >= 0 && parent.Newer >= 0)
        {
            return []; // unchanged: nothing to show
        }

        // An index of -1 is "not in that snapshot" here, never "the top level".
        var path = IgnorePath(parent);
        return area == DiffArea.FileSystem
            ? Merge(area, value: false, Kids(parent.Older, () => FsChildren(Diff.Older, parent.Older, folders: true)), Kids(parent.Newer, () => FsChildren(Diff.Newer, parent.Newer, folders: true)), path, container: true)
            : Merge(area, value: false, Kids(parent.Older, () => KeyChildren(Diff.Older, parent.Older)), Kids(parent.Newer, () => KeyChildren(Diff.Newer, parent.Newer)), path, container: true);
    }

    private static List<(string Name, int Index)> Kids(int index, Func<List<(string Name, int Index)>> read) => index >= 0 ? read() : [];

    /// <summary>Cheap check for the + box: whether a folder or key may hold folders or keys to show.</summary>
    public bool ContainerHasChildren(ViewItem item)
    {
        if (item.IsCategory)
        {
            return CategoryHasChildren(item.Area);
        }

        var area = item.Area;
        if (!ShowAll && item.Node >= 0 && !IsWhole(area, item.Node))
        {
            var tree = Diff.Tree(area);
            return tree.Children(item.Node).Any(c => IsContainer(tree.Kind(c)));
        }

        if (!ShowAll && item.Older >= 0 && item.Newer >= 0)
        {
            return false;
        }

        return area == DiffArea.FileSystem
            ? (item.Older >= 0 && Diff.Older.FileSystem.ChildFolders[item.Older] > 0) || (item.Newer >= 0 && Diff.Newer.FileSystem.ChildFolders[item.Newer] > 0)
            : (item.Older >= 0 && Diff.Older.Registry.Descendants[item.Older] > 0) || (item.Newer >= 0 && Diff.Newer.Registry.Descendants[item.Newer] > 0);
    }

    // ---- Files, values and services (the list) ----

    /// <summary>The files or values directly in a folder or key, sorted by name; every service for the Services category.</summary>
    public List<ViewItem> Leaves(ViewItem parent)
    {
        var area = parent.Area;
        if (parent.IsCategory)
        {
            if (area != DiffArea.Services)
            {
                return [];
            }

            return ShowAll
                ? Merge(area, value: false, Services(Diff.Older), Services(Diff.Newer), string.Empty, container: false)
                : Sorted([.. Diff.Services.Roots().Select(n => FromNode(area, n))]);
        }

        if (!ShowAll && parent.Node >= 0 && !IsWhole(area, parent.Node))
        {
            var tree = Diff.Tree(area);
            return Sorted([.. tree.Children(parent.Node).Where(c => !IsContainer(tree.Kind(c))).Select(c => FromNode(area, c))]);
        }

        if (!ShowAll && parent.Older >= 0 && parent.Newer >= 0)
        {
            return [];
        }

        var path = IgnorePath(parent);
        return area == DiffArea.FileSystem
            ? Merge(area, value: false, Kids(parent.Older, () => FsChildren(Diff.Older, parent.Older, folders: false)), Kids(parent.Newer, () => FsChildren(Diff.Newer, parent.Newer, folders: false)), path, container: false)
            : Merge(area, value: true, Values(Diff.Older, parent.Older), Values(Diff.Newer, parent.Newer), path, container: false);
    }

    private ViewItem FromNode(DiffArea area, int node)
    {
        var tree = Diff.Tree(area);
        var older = tree.OlderIndex(node);
        var newer = tree.NewerIndex(node);
        return new ViewItem(area, older, newer, node, StatusOf(area, older, newer, node), tree.Kind(node) == DiffItemKind.Value);
    }

    private List<ViewItem> Sorted(List<ViewItem> items)
    {
        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Name(a), Name(b)));
        return items;
    }

    /// <summary>
    /// Pairs the children of the older and newer side by name (as the comparison does, ignoring case) and finds
    /// each one's comparison node. Children left out by the Global Ignore list are skipped.
    /// </summary>
    private List<ViewItem> Merge(DiffArea area, bool value, List<(string Name, int Index)> olderList, List<(string Name, int Index)> newerList, string parentPath, bool container)
    {
        var newerByName = new Dictionary<string, int>(newerList.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in newerList)
        {
            newerByName.TryAdd(name, index);
        }

        var pairs = new List<(string Name, int Older, int Newer)>(Math.Max(olderList.Count, newerList.Count));
        foreach (var (name, o) in olderList)
        {
            if (newerByName.Remove(name, out var n))
            {
                pairs.Add((name, o, n));
            }
            else
            {
                pairs.Add((name, o, -1));
            }
        }

        foreach (var (name, n) in newerList)
        {
            if (newerByName.Remove(name))
            {
                pairs.Add((name, -1, n));
            }
        }

        var checkIgnore = Diff.Ignore.RuleCount > 0;
        var target = area switch
        {
            DiffArea.FileSystem => IgnoreTarget.FileSystem,
            DiffArea.Registry => IgnoreTarget.Registry,
            _ => IgnoreTarget.Service,
        };
        var items = new List<ViewItem>(pairs.Count);
        foreach (var (name, o, n) in pairs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (checkIgnore && Diff.Ignore.IsIgnoredEntry(target, parentPath.Length == 0 ? name : parentPath + "\\" + name))
            {
                continue;
            }

            var node = container ? ContainerNode(area, o, n) : LeafNode(area, o, n);
            items.Add(new ViewItem(area, o, n, node, StatusOf(area, o, n, node), value));
        }

        return items;
    }

    /// <summary>A folder's or key's path for joining child names in ignore checks ("C:" for a drive root).</summary>
    private string IgnorePath(ViewItem parent)
    {
        if (Diff.Ignore.RuleCount == 0)
        {
            return string.Empty;
        }

        var path = Path(parent);
        return path.Length == 3 && path[1] == ':' ? path[..2] : path;
    }

    private static List<(string Name, int Index)> FsChildren(Snapshot snapshot, int parent, bool folders)
    {
        var fs = snapshot.FileSystem;
        var list = new List<(string, int)>();
        if (parent < 0 && !folders)
        {
            return list;
        }

        var (start, end) = parent < 0 ? (0, fs.Count - 1) : (parent + 1, parent + fs.Descendants[parent]);
        for (var c = start; c <= end; c += fs.Descendants[c] + 1)
        {
            if (fs.IsDirectory(c) == folders || (parent < 0 && fs.Parent[c] < 0))
            {
                list.Add((snapshot.Names.GetString(fs.Name[c]), c));
            }
        }

        return list;
    }

    private static List<(string Name, int Index)> KeyChildren(Snapshot snapshot, int parent)
    {
        var reg = snapshot.Registry;
        var list = new List<(string, int)>();
        var (start, end) = parent < 0 ? (0, reg.KeyCount - 1) : (parent + 1, parent + reg.Descendants[parent]);
        for (var c = start; c <= end; c += reg.Descendants[c] + 1)
        {
            list.Add((snapshot.Names.GetString(reg.Name[c]), c));
        }

        return list;
    }

    private static List<(string Name, int Index)> Values(Snapshot snapshot, int key)
    {
        var list = new List<(string, int)>();
        if (key < 0)
        {
            return list;
        }

        var reg = snapshot.Registry;
        for (var v = reg.FirstValue[key]; v < reg.FirstValue[key] + reg.KeyValueCount[key]; v++)
        {
            list.Add((snapshot.Names.GetString(reg.ValueName[v]), v));
        }

        return list;
    }

    private static List<(string Name, int Index)> Services(Snapshot snapshot)
    {
        var services = snapshot.Services;
        var list = new List<(string, int)>(services.Count);
        for (var i = 0; i < services.Count; i++)
        {
            list.Add((snapshot.Names.GetString(services.Name[i]), i));
        }

        return list;
    }

    // ---- Names, paths and values ----

    /// <summary>The snapshot an item is read from (the older one when it is in both) and its index there.</summary>
    private (Snapshot Snapshot, int Index) Source(ViewItem item) =>
        item.Older >= 0 ? (Diff.Older, item.Older) : (Diff.Newer, item.Newer);

    /// <summary>The item's index in the left- or right-hand snapshot, or -1.</summary>
    public int IndexOn(ViewItem item, bool right) =>
        ReferenceEquals(right ? Diff.Right : Diff.Left, Diff.Newer) ? item.Newer : item.Older;

    /// <summary>The plain name: "Program Files", "C:" for a drive, "" for a default value, a service's key name.</summary>
    public string Name(ViewItem item)
    {
        if (item.IsCategory)
        {
            return CategoryName(item.Area);
        }

        var (snapshot, index) = Source(item);
        var id = item.Area switch
        {
            DiffArea.FileSystem => snapshot.FileSystem.Name[index],
            DiffArea.Registry when item.IsValue => snapshot.Registry.ValueName[index],
            DiffArea.Registry => snapshot.Registry.Name[index],
            _ => snapshot.Services.Name[index],
        };
        return snapshot.Names.GetString(id);
    }

    /// <summary>The name as shown: "C:\" for a drive, "(Default)", "HKCU  (S-1-5-…)", "Print Spooler  (Spooler)".</summary>
    public string DisplayName(ViewItem item)
    {
        var name = Name(item);
        if (item.IsCategory)
        {
            return name;
        }

        var (snapshot, index) = Source(item);
        switch (item.Area)
        {
            case DiffArea.FileSystem when snapshot.FileSystem.Parent[index] < 0:
                return name + "\\";
            case DiffArea.Registry when item.IsValue:
                return name.Length == 0 ? "(Default)" : name;
            case DiffArea.Registry:
                var reg = snapshot.Registry;
                var parent = reg.Parent[index];
                return parent >= 0 && reg.Parent[parent] < 0 && snapshot.Names.GetString(reg.Name[parent]) == "HKU"
                    && string.Equals(name, currentUserSid, StringComparison.OrdinalIgnoreCase)
                    ? $"HKCU  ({name})"
                    : name;
            case DiffArea.Services:
                var display = snapshot.Names.GetString(snapshot.Services.DisplayName[index]);
                return string.IsNullOrEmpty(display) || display == name ? name : $"{display}  ({name})";
            default:
                return name;
        }
    }

    /// <summary>Full path of a folder, file or key ("C:\Program Files\Contoso", "HKLM\SOFTWARE"); a service's name.</summary>
    public string Path(ViewItem item)
    {
        if (item.IsCategory)
        {
            return CategoryName(item.Area);
        }

        var (snapshot, index) = Source(item);
        return item.Area switch
        {
            DiffArea.FileSystem => snapshot.FileSystem.GetFullPath(index, snapshot.Names),
            DiffArea.Registry when !item.IsValue => snapshot.Registry.GetFullPath(index, snapshot.Names),
            DiffArea.Registry => Name(item),
            _ => Name(item),
        };
    }

    public static string CategoryName(DiffArea area) => area switch
    {
        DiffArea.FileSystem => "File system",
        DiffArea.Registry => "Registry",
        _ => "Services",
    };

    /// <summary>"File system  (3 new, 1 modified, 0 deleted)".</summary>
    public string CategoryText(DiffArea area)
    {
        var counts = area switch
        {
            DiffArea.FileSystem => Diff.FileSystemCounts,
            DiffArea.Registry => Diff.RegistryCounts,
            _ => Diff.ServicesCounts,
        };
        var name = CategoryName(area);
        return counts.Total == 0
            ? $"{name}  (no changes)"
            : string.Create(CultureInfo.CurrentCulture, $"{name}  ({counts.New:N0} new, {counts.Modified:N0} modified, {counts.Deleted:N0} deleted)");
    }

    /// <summary>The count after a folder or key in the tree: changes inside it, or the items of a whole new or deleted one.</summary>
    public string CountText(ViewItem item)
    {
        if (item.Node >= 0)
        {
            var tree = Diff.Tree(item.Area);
            var count = tree.ChangedItems(item.Node);
            return item.Status switch
            {
                RowStatus.Inside => string.Create(CultureInfo.CurrentCulture, $"  ({count:N0})"),
                RowStatus.Modified when tree.Descendants(item.Node) > 0 => string.Create(CultureInfo.CurrentCulture, $"  ({count - 1:N0})"),
                RowStatus.New or RowStatus.Deleted when count > 1 => string.Create(CultureInfo.CurrentCulture, $"  ({count:N0} items)"),
                _ => string.Empty,
            };
        }

        if (item.Status is RowStatus.New or RowStatus.Deleted)
        {
            var (snapshot, index) = Source(item);
            long items = item.Area == DiffArea.FileSystem
                ? snapshot.FileSystem.Descendants[index]
                : snapshot.Registry.Descendants[index] + CountValues(snapshot.Registry, index);
            return items > 0 ? string.Create(CultureInfo.CurrentCulture, $"  ({items:N0} items)") : string.Empty;
        }

        return string.Empty;
    }

    private static long CountValues(RegTree reg, int key)
    {
        long total = 0;
        for (var k = key; k <= key + reg.Descendants[key]; k++)
        {
            total += reg.KeyValueCount[k];
        }

        return total;
    }

    public static string Sign(RowStatus status) => status switch
    {
        RowStatus.New => "+",
        RowStatus.Deleted => "−",
        RowStatus.Modified => "~",
        _ => string.Empty,
    };

    /// <summary>Text colour: green, orange, red, blue for changes inside; null (normal text) when the same.</summary>
    public static Color? TextColor(RowStatus status)
    {
        var p = ThemeManager.Current;
        return status switch
        {
            RowStatus.New => p.New,
            RowStatus.Modified => p.Modified,
            RowStatus.Deleted => p.Deleted,
            RowStatus.Inside => p.Inside,
            _ => null,
        };
    }

    /// <summary>Icon colour: as the text, secondary text when the same.</summary>
    public static Color IconColor(RowStatus status) => TextColor(status) ?? ThemeManager.Current.SecondaryText;

    /// <summary>The changed fields of a modified file, value or service.</summary>
    public ChangedFields Fields(ViewItem item) =>
        item.Node >= 0 && item.Status == RowStatus.Modified ? Diff.Tree(item.Area).Fields(item.Node) : ChangedFields.None;

    /// <summary>One line of the details panel: a property, its value on the left and right, and whether it differs.</summary>
    internal readonly record struct DetailRow(string Label, string Left, string Right, bool Changed);

    /// <summary>The properties of an item on both sides, for the details panel.</summary>
    public List<DetailRow> Details(ViewItem item)
    {
        var rows = new List<DetailRow>();
        if (item.IsCategory)
        {
            return rows;
        }

        var left = IndexOn(item, right: false);
        var right = IndexOn(item, right: true);
        var fields = Fields(item);
        string On(bool r, Func<Snapshot, int, string> read)
        {
            var index = r ? right : left;
            return index < 0 ? "(does not exist)" : read(r ? Diff.Right : Diff.Left, index);
        }

        void Add(string label, Func<Snapshot, int, string> read, bool changed) =>
            rows.Add(new DetailRow(label, On(false, read), On(true, read), changed));

        var bothSides = left >= 0 && right >= 0;
        switch (item.Area)
        {
            case DiffArea.FileSystem when IsFolder(item):
                Add("Date modified", (s, i) => ItemFormat.Time(s.FileSystem.LastWriteTime[i]), false);
                Add("Contains", (s, i) => $"{DiffItems.Count(s.FileSystem.ChildFolders[i], "folder")}, {DiffItems.Count(s.FileSystem.ChildFiles[i], "file")}", false);
                Add("Attributes", (s, i) => ItemFormat.Attributes(s.FileSystem.Attributes[i]), fields.HasFlag(ChangedFields.Attributes));
                break;
            case DiffArea.FileSystem:
                Add("Size", (s, i) => string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Size(s.FileSystem.Size[i])}  ({s.FileSystem.Size[i]:N0} bytes)"), fields.HasFlag(ChangedFields.Size));
                Add("Date modified", (s, i) => ItemFormat.Time(s.FileSystem.LastWriteTime[i]), fields.HasFlag(ChangedFields.LastWriteTime));
                Add("Attributes", (s, i) => ItemFormat.Attributes(s.FileSystem.Attributes[i]), fields.HasFlag(ChangedFields.Attributes));
                break;
            case DiffArea.Registry when item.IsValue:
                Add("Type", (s, i) => ItemFormat.ValueType(s.Registry.ValueDataType[i]), fields.HasFlag(ChangedFields.ValueType));
                Add("Data", (s, i) => Shorten(ItemFormat.ValueData(s.Registry, i, full: true)), fields.HasFlag(ChangedFields.ValueData));
                break;
            case DiffArea.Registry:
                Add("Last written", (s, i) => ItemFormat.Time(s.Registry.LastWriteTime[i]), false);
                Add("Contains", (s, i) => $"{DiffItems.Count(SubkeyCount(s.Registry, i), "key")}, {DiffItems.Count(s.Registry.KeyValueCount[i], "value")}", false);
                break;
            default:
                Add("Display name", (s, i) => s.Names.GetString(s.Services.DisplayName[i]), fields.HasFlag(ChangedFields.DisplayName));
                Add("Status", (s, i) => ItemFormat.ServiceState(s.Services.State[i]), fields.HasFlag(ChangedFields.State));
                Add("Start type", (s, i) => ItemFormat.StartType(s.Services.StartType[i], s.Services.Flags[i]), fields.HasFlag(ChangedFields.StartType) || fields.HasFlag(ChangedFields.ServiceFlags));
                break;
        }

        if (!bothSides)
        {
            // Only one side has it: nothing to compare, nothing marked.
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i] = rows[i] with { Changed = false };
            }
        }

        return rows;
    }

    public bool IsFolder(ViewItem item)
    {
        if (item.Area != DiffArea.FileSystem || item.IsCategory)
        {
            return false;
        }

        var (snapshot, index) = Source(item);
        return snapshot.FileSystem.IsDirectory(index);
    }

    public static int SubkeyCount(RegTree reg, int key)
    {
        var keys = 0;
        for (var c = key + 1; c <= key + reg.Descendants[key]; c += reg.Descendants[c] + 1)
        {
            keys++;
        }

        return keys;
    }

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + " …";

    // ---- List columns ----

    /// <summary>The value a list column shows: from the right-hand snapshot, or the left one when the item is only there.</summary>
    public (Snapshot Snapshot, int Index) Shown(ViewItem item)
    {
        var right = IndexOn(item, right: true);
        return right >= 0 ? (Diff.Right, right) : (Diff.Left, IndexOn(item, right: false));
    }
}
