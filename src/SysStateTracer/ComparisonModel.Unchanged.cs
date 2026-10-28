using System.Globalization;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// "Show unchanged" (GitHub issue #4): when a category, folder or key is opened, the items that are the same
/// in both snapshots are listed too, in normal text and without a change sign. Nothing extra is read or
/// compared until something is opened, so the comparison itself stays as fast as before. An unchanged row's
/// id holds bit 58, the area in bits 55–56, bit 54 for a registry value, and its index in the older (bits
/// 27–53) and newer (bits 0–26) snapshot, so the same item has the same id after Swap or a new model.
/// </summary>
internal sealed partial class ComparisonModel
{
    private const long UnchangedFlag = 1L << 58;
    private const long UnchangedValueFlag = 1L << 54;
    private const int IndexBits = 27;
    private const long IndexMask = (1L << IndexBits) - 1;

    /// <summary>For each unchanged value row: its key in the older snapshot.</summary>
    private readonly Dictionary<long, int> _unchangedValueKey = [];

    /// <summary>List the items that did not change inside what is opened (off by default).</summary>
    public bool ShowUnchanged { get; init; }

    /// <summary>True for an item that is the same in both snapshots (only listed with Show unchanged).</summary>
    public static bool IsUnchanged(long id) => id > 0 && (id & UnchangedFlag) != 0 && (id & InsideFlag) == 0;

    /// <summary>A row for information only: inside a whole new or deleted folder or key, or unchanged.</summary>
    public static bool IsExtra(long id) => IsInside(id) || IsUnchanged(id);

    /// <summary>The area of an extra row.</summary>
    public static DiffArea ExtraArea(long id) => IsInside(id) ? InsideArea(id) : (DiffArea)((id >> 55) & 3);

    private static int UnchangedOlder(long id) => (int)((id >> IndexBits) & IndexMask);

    private static int UnchangedNewer(long id) => (int)(id & IndexMask);

    private static bool IsUnchangedValue(long id) => (id & UnchangedValueFlag) != 0;

    private static long UnchangedId(DiffArea area, bool value, int older, int newer) =>
        older > IndexMask || newer > IndexMask
            ? -1
            : UnchangedFlag | ((long)area << 55) | (value ? UnchangedValueFlag : 0) | ((long)older << IndexBits) | (uint)newer;

    /// <summary>What Jump to opens for an extra row: the file or folder, or the key (for a value, its key).</summary>
    public string JumpPath(long id) =>
        IsUnchanged(id) && IsUnchangedValue(id)
            ? Diff.Older.Registry.GetFullPath(_unchangedValueKey[id], Diff.Older.Names)
            : ExtraPath(id);

    /// <summary>The full path of an extra row (a value as "key\name", or "key|name" for row keys); a service's name.</summary>
    public string ExtraPath(long id, bool forRowKey = false)
    {
        if (IsInside(id))
        {
            return InsidePath(id);
        }

        var older = Diff.Older;
        var index = UnchangedOlder(id);
        switch (ExtraArea(id))
        {
            case DiffArea.FileSystem:
                return older.FileSystem.GetFullPath(index, older.Names);
            case DiffArea.Registry when IsUnchangedValue(id):
                var key = older.Registry.GetFullPath(_unchangedValueKey[id], older.Names);
                var name = older.Names.GetString(older.Registry.ValueName[index]);
                return forRowKey ? $"{key}|{name}" : $"{key}\\{name}";
            case DiffArea.Registry:
                return older.Registry.GetFullPath(index, older.Names);
            default:
                return older.Names.GetString(older.Services.Name[index]);
        }
    }

    /// <summary>The older and newer index of a category (-1, -1) or a folder or key that exists on both sides.</summary>
    private (int Older, int Newer)? OnBothSides(DiffItem item)
    {
        if (DiffActions.IsCategory(item))
        {
            return (-1, -1);
        }

        var tree = Diff.Tree(item.Area);
        if (tree.Kind(item.Node) is not (DiffItemKind.Folder or DiffItemKind.Key))
        {
            return null;
        }

        var older = tree.IsSwapped ? tree.Right(item.Node) : tree.Left(item.Node);
        var newer = tree.IsSwapped ? tree.Left(item.Node) : tree.Right(item.Node);
        return older >= 0 && newer >= 0 ? (older, newer) : null;
    }

    private bool UnchangedHolds(DiffArea area, int older, int newer) => area switch
    {
        _ when older < 0 => area switch
        {
            DiffArea.FileSystem => Diff.Older.FileSystem.Count + Diff.Newer.FileSystem.Count > 0,
            DiffArea.Registry => Diff.Older.Registry.KeyCount + Diff.Newer.Registry.KeyCount > 0,
            _ => Diff.Older.Services.Count + Diff.Newer.Services.Count > 0,
        },
        DiffArea.FileSystem => Diff.Older.FileSystem.Descendants[older] > 0 || Diff.Newer.FileSystem.Descendants[newer] > 0,
        _ => HoldsAnything(DiffArea.Registry, Diff.Older, older) || HoldsAnything(DiffArea.Registry, Diff.Newer, newer),
    };

    private bool UnchangedHasChildren(long id)
    {
        var area = ExtraArea(id);
        if (area == DiffArea.Services || IsUnchangedValue(id))
        {
            return false;
        }

        var older = UnchangedOlder(id);
        return area == DiffArea.FileSystem
            ? Diff.Older.FileSystem.IsDirectory(older) && Diff.Older.FileSystem.Descendants[older] > 0
            : HoldsAnything(DiffArea.Registry, Diff.Older, older);
    }

    /// <summary>
    /// The changed children <paramref name="changed"/> of a category, folder or key, with the children that are
    /// the same on both sides added, sorted by name (subkeys before values). Items left out by the Global
    /// Ignore list are in neither list, so they stay hidden.
    /// </summary>
    private List<long> WithUnchanged(DiffArea area, int olderParent, int newerParent, IReadOnlyList<int> changed)
    {
        var tree = Diff.Tree(area);
        var older = Diff.Older;
        var newer = Diff.Newer;
        var rows = new List<(int Group, string Name, long Id)>();
        var byOlder = new Dictionary<(bool Value, int Index), int>();
        var byNewer = new Dictionary<(bool Value, int Index), int>();
        foreach (var node in changed)
        {
            var value = tree.Kind(node) == DiffItemKind.Value;
            var o = tree.IsSwapped ? tree.Right(node) : tree.Left(node);
            var n = tree.IsSwapped ? tree.Left(node) : tree.Right(node);
            if (o >= 0)
            {
                byOlder[(value, o)] = node;
            }

            if (n >= 0)
            {
                byNewer[(value, n)] = node;
            }
        }

        void Pair(bool value, int group, List<(string Name, int Index)> olderList, List<(string Name, int Index)> newerList, Func<int, int, bool> sameKind, string parentPath, IgnoreTarget target)
        {
            var newerByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, index) in newerList)
            {
                newerByName.TryAdd(name, index);
            }

            foreach (var (name, o) in olderList)
            {
                if (byOlder.ContainsKey((value, o)) || !newerByName.TryGetValue(name, out var n) || byNewer.ContainsKey((value, n)) || !sameKind(o, n))
                {
                    continue; // changed (listed from the comparison), or only on one side
                }

                var path = parentPath.Length == 0 ? name : parentPath + "\\" + name;
                if (Diff.Ignore.RuleCount > 0 && Diff.Ignore.IsIgnoredEntry(target, path))
                {
                    continue;
                }

                var id = UnchangedId(area, value, o, n);
                if (id > 0)
                {
                    if (value)
                    {
                        _unchangedValueKey[id] = olderParent;
                    }

                    rows.Add((group, name, id));
                }
            }
        }

        switch (area)
        {
            case DiffArea.FileSystem:
            {
                var parentPath = olderParent < 0 ? string.Empty : TrimRoot(older.FileSystem.GetFullPath(olderParent, older.Names));
                Pair(false, 0, FsChildren(older, olderParent), FsChildren(newer, newerParent), (o, n) => older.FileSystem.IsDirectory(o) == newer.FileSystem.IsDirectory(n), parentPath, IgnoreTarget.FileSystem);
                break;
            }

            case DiffArea.Registry:
            {
                var parentPath = olderParent < 0 ? string.Empty : older.Registry.GetFullPath(olderParent, older.Names);
                Pair(false, 0, KeyChildren(older, olderParent), KeyChildren(newer, newerParent), (_, _) => true, parentPath, IgnoreTarget.Registry);
                if (olderParent >= 0)
                {
                    Pair(true, 1, Values(older, olderParent), Values(newer, newerParent), (_, _) => true, parentPath, IgnoreTarget.Registry);
                }

                break;
            }

            default:
                Pair(false, 0, Services(older), Services(newer), (_, _) => true, string.Empty, IgnoreTarget.Service);
                break;
        }

        foreach (var node in changed)
        {
            var group = tree.Kind(node) == DiffItemKind.Value ? 1 : 0;
            rows.Add((group, Diff.Name(tree, node), NodeId(area, node)));
        }

        rows.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return [.. rows.Select(r => r.Id)];
    }

    /// <summary>"C:\" → "C:", so children join as "C:\name" like the comparison's paths.</summary>
    private static string TrimRoot(string path) => path.Length == 3 && path[1] == ':' ? path[..2] : path;

    private static List<(string Name, int Index)> FsChildren(Snapshot snapshot, int parent)
    {
        var fs = snapshot.FileSystem;
        var list = new List<(string, int)>();
        var (start, end) = parent < 0 ? (0, fs.Count - 1) : (parent + 1, parent + fs.Descendants[parent]);
        for (var c = start; c <= end; c += fs.Descendants[c] + 1)
        {
            list.Add((snapshot.Names.GetString(fs.Name[c]), c));
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
        var reg = snapshot.Registry;
        var list = new List<(string, int)>();
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

    private string UnchangedText(long id, int column)
    {
        if (column == StatusColumn)
        {
            return string.Empty;
        }

        var area = ExtraArea(id);
        var value = IsUnchangedValue(id);
        if (column == NameColumn)
        {
            var older = Diff.Older;
            var index = UnchangedOlder(id);
            switch (area)
            {
                case DiffArea.FileSystem:
                {
                    var name = older.Names.GetString(older.FileSystem.Name[index]);
                    return older.FileSystem.Parent[index] < 0 ? name + "\\" : name;
                }

                case DiffArea.Registry when value:
                {
                    var name = older.Names.GetString(older.Registry.ValueName[index]);
                    return name.Length == 0 ? "(Default)" : name;
                }

                case DiffArea.Registry:
                    return older.Names.GetString(older.Registry.Name[index]);
                default:
                {
                    var name = older.Names.GetString(older.Services.Name[index]);
                    var display = older.Names.GetString(older.Services.DisplayName[index]);
                    return string.IsNullOrEmpty(display) || display == name ? name : $"{display}  ({name})";
                }
            }
        }

        // The left and right columns: each from its own snapshot.
        var right = column == RightColumn;
        var snapshot = right ? Diff.Right : Diff.Left;
        var i = snapshot == Diff.Older ? UnchangedOlder(id) : UnchangedNewer(id);
        switch (area)
        {
            case DiffArea.FileSystem:
            {
                var fs = snapshot.FileSystem;
                return fs.IsDirectory(i)
                    ? string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Time(fs.LastWriteTime[i])} · {DiffItems.Count(fs.ChildFolders[i], "folder")}, {DiffItems.Count(fs.ChildFiles[i], "file")}")
                    : $"{ItemFormat.Time(fs.LastWriteTime[i])} · {ItemFormat.Size(fs.Size[i])}";
            }

            case DiffArea.Registry when value:
                return $"{ItemFormat.ValueType(snapshot.Registry.ValueDataType[i])} · {ItemFormat.ValueData(snapshot.Registry, i)}";
            case DiffArea.Registry:
            {
                var reg = snapshot.Registry;
                var keys = 0;
                for (var c = i + 1; c <= i + reg.Descendants[i]; c += reg.Descendants[c] + 1)
                {
                    keys++;
                }

                return string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Time(reg.LastWriteTime[i])} · {DiffItems.Count(keys, "key")}, {DiffItems.Count(reg.KeyValueCount[i], "value")}");
            }

            default:
                return $"{ItemFormat.ServiceState(snapshot.Services.State[i])} · {ItemFormat.StartType(snapshot.Services.StartType[i], snapshot.Services.Flags[i])}";
        }
    }

    private string? UnchangedIcon(long id) => ExtraArea(id) switch
    {
        DiffArea.FileSystem => Diff.Older.FileSystem.Parent[UnchangedOlder(id)] < 0
            ? Glyphs.Drive
            : Diff.Older.FileSystem.IsDirectory(UnchangedOlder(id)) ? Glyphs.Folder : Glyphs.File,
        DiffArea.Registry => IsUnchangedValue(id) ? Glyphs.Value : Glyphs.Folder,
        _ => null,
    };
}
