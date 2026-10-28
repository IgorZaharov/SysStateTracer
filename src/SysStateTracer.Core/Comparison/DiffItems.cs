using System.Globalization;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Comparison;

/// <summary>The three parts of a comparison.</summary>
public enum DiffArea : byte
{
    FileSystem,
    Registry,
    Services,
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
                    $"{ItemFormat.Time(fs.LastWriteTime[index])} · {fs.ChildFolders[index]:N0} folders, {fs.ChildFiles[index]:N0} files");
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
                    $"{ItemFormat.Time(reg.LastWriteTime[index])} · {keys:N0} keys, {reg.KeyValueCount[index]:N0} values");
            }

            case DiffItemKind.Value:
            {
                var reg = snapshot.Registry;
                return $"{ItemFormat.ValueType(reg.ValueDataType[index])} · {ItemFormat.ValueData(reg, index)}";
            }

            default:
            {
                var services = snapshot.Services;
                return $"{ItemFormat.ServiceState(services.State[index])} · {ItemFormat.StartType(services.StartType[index], services.Flags[index])}";
            }
        }
    }

    /// <summary>The Global Ignore rule that hides this item: "FS:path", "REG:key[\value]" or "SVC:name".</summary>
    public static string IgnoreRule(DiffResult diff, DiffItem item)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var tree = diff.Tree(item.Area);
        return item.Area switch
        {
            DiffArea.FileSystem => "FS:" + diff.FullPath(tree, item.Node),
            DiffArea.Registry => "REG:" + diff.FullPath(tree, item.Node),
            _ => "SVC:" + diff.Name(tree, item.Node),
        };
    }
}
