using System.Globalization;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Comparison;

/// <summary>The parts of a comparison.</summary>
public enum DiffArea : byte
{
    FileSystem,
    Registry,
    Services,

    /// <summary>System and User environment variables.</summary>
    Environment,
}

/// <summary>A node of one part of a comparison.</summary>
public readonly record struct DiffItem(DiffArea Area, int Node);

/// <summary>
/// "Mark as non-important" (plan §10.6): marked items are struck through and left out of exports,
/// together with everything below them. Kept only while the comparison window is open (decision D5).
/// </summary>
public sealed class ImportanceMarks
{
    private readonly HashSet<DiffItem> _marked = [];

    public int Count => _marked.Count;

    /// <summary>The marked items themselves (not what lies below them).</summary>
    public IReadOnlyCollection<DiffItem> Items => _marked;

    public void Clear() => _marked.Clear();

    public bool IsMarked(DiffItem item) => _marked.Contains(item);

    public bool IsMarked(DiffArea area, int node) => _marked.Contains(new DiffItem(area, node));

    /// <summary>Marks the items if any of them is unmarked, otherwise unmarks them all (one menu click).</summary>
    public void Toggle(IEnumerable<DiffItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.All(_marked.Contains))
        {
            _marked.ExceptWith(list);
        }
        else
        {
            _marked.UnionWith(list);
        }
    }

    /// <summary>
    /// <see cref="Toggle(IEnumerable{DiffItem})"/> for a menu click in the comparison window. Marking also marks
    /// each unchanged parent whose changes are now all marked, up the tree, so a branch with nothing else
    /// left in it is struck through as a whole. Unmarking also unmarks the parents of the items.
    /// </summary>
    public void Toggle(DiffResult diff, IEnumerable<DiffItem> items)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Count > 0 && list.All(_marked.Contains))
        {
            _marked.ExceptWith(list);
            foreach (var item in list)
            {
                var tree = diff.Tree(item.Area);
                for (var p = tree.Parent(item.Node); p >= 0; p = tree.Parent(p))
                {
                    _marked.Remove(item with { Node = p });
                }
            }

            return;
        }

        _marked.UnionWith(list);
        foreach (var item in list)
        {
            var tree = diff.Tree(item.Area);
            for (var p = tree.Parent(item.Node); p >= 0; p = tree.Parent(p))
            {
                var parent = item with { Node = p };
                if (_marked.Contains(parent))
                {
                    continue;
                }

                if (tree.Change(p) != ChangeKind.Unchanged
                    || !tree.Children(p).All(c => IsMarkedOrInside(diff, item with { Node = c })))
                {
                    break;
                }

                _marked.Add(parent);
            }
        }
    }

    /// <summary>True when the node or one of its parents is marked.</summary>
    public bool IsMarkedOrInside(DiffResult diff, DiffItem item)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (_marked.Count == 0)
        {
            return false;
        }

        var tree = diff.Tree(item.Area);
        for (var n = item.Node; n >= 0; n = tree.Parent(n))
        {
            if (_marked.Contains(item with { Node = n }))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Text of the snapshot columns and other per-item details (plan §10.4).</summary>
public static class DiffItems
{
    /// <summary>
    /// What the left (<paramref name="right"/> false) or right snapshot column shows for an item,
    /// or an empty string when the item is not in that snapshot.
    /// </summary>
    public static string Column(DiffResult diff, DiffItem item, bool right)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var tree = diff.Tree(item.Area);
        var snapshot = right ? diff.Right : diff.Left;
        var index = right ? tree.Right(item.Node) : tree.Left(item.Node);
        if (index < 0)
        {
            return string.Empty;
        }

        switch (tree.Kind(item.Node))
        {
            case DiffItemKind.Folder:
            {
                var fs = snapshot.FileSystem;
                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"{ItemFormat.Time(fs.LastWriteTime[index])} · {Count(fs.ChildFolders[index], "folder")}, {Count(fs.ChildFiles[index], "file")}");
            }

            case DiffItemKind.File:
            {
                var fs = snapshot.FileSystem;
                var text = $"{ItemFormat.Time(fs.LastWriteTime[index])} · {ItemFormat.Size(fs.Size[index])}";
                return tree.Fields(item.Node).HasFlag(ChangedFields.Attributes)
                    ? $"{text} · {ItemFormat.Attributes(fs.Attributes[index])}"
                    : text;
            }

            case DiffItemKind.Key:
            {
                var reg = snapshot.Registry;
                var keys = 0;
                for (var c = index + 1; c <= index + reg.Descendants[index]; c += reg.Descendants[c] + 1)
                {
                    keys++;
                }

                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"{ItemFormat.Time(reg.LastWriteTime[index])} · {Count(keys, "key")}, {Count(reg.KeyValueCount[index], "value")}");
            }

            case DiffItemKind.Value:
            {
                var reg = snapshot.Registry;
                return $"{ItemFormat.ValueType(reg.ValueDataType[index])} · {ItemFormat.ValueData(reg, index)}";
            }

            case DiffItemKind.Variable:
            {
                var env = snapshot.Environment;
                return $"{ItemFormat.ValueType(env.ValueType[index])} · {snapshot.Names.GetString(env.Value[index])}";
            }

            default:
            {
                var services = snapshot.Services;
                return $"{ItemFormat.ServiceState(services.State[index])} · {ItemFormat.StartType(services.StartType[index], services.Flags[index])}";
            }
        }
    }

    /// <summary>"1 file", "12 files".</summary>
    public static string Count(long count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    /// <summary>The Global Ignore rule that hides this item: "FS:path", "REG:key[\value]", "SVC:name" or "ENV:User\name".</summary>
    public static string IgnoreRule(DiffResult diff, DiffItem item)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var tree = diff.Tree(item.Area);
        return item.Area switch
        {
            DiffArea.FileSystem => "FS:" + diff.FullPath(tree, item.Node),
            DiffArea.Registry => "REG:" + diff.FullPath(tree, item.Node),
            DiffArea.Environment => "ENV:" + diff.FullPath(tree, item.Node),
            _ => "SVC:" + diff.Name(tree, item.Node),
        };
    }
}
