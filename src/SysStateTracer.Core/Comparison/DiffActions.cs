namespace SysStateTracer.Core.Comparison;

/// <summary>The actions of the comparison window's context menu (plan §10.6).</summary>
public enum DiffAction
{
    JumpTo,
    MarkNonImportant,
    AddToIgnoreList,
    ExportReg,
    CopyToFiles,
    RevertValue,
}

/// <summary>
/// When each context-menu action is available (plan §10.6, F3). A selection holds comparison nodes and
/// possibly whole categories ("File system", "Registry", "Services"), given as items with <c>Node = -1</c>.
/// </summary>
public static class DiffActions
{
    /// <summary>A whole category row of the comparison window.</summary>
    public static DiffItem Category(DiffArea area) => new(area, -1);

    public static bool IsCategory(DiffItem item) => item.Node < 0;

    public static bool IsEnabled(DiffAction action, IReadOnlyCollection<DiffItem> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0)
        {
            return false;
        }

        var nodesOnly = selection.All(i => !IsCategory(i));
        return action switch
        {
            // One item, which has a place to open: a folder, file, key, value or service.
            DiffAction.JumpTo => selection.Count == 1 && nodesOnly,

            DiffAction.MarkNonImportant or DiffAction.AddToIgnoreList => nodesOnly,

            // A whole registry category or hive exports everything changed in it (F3).
            DiffAction.ExportReg => selection.All(i => i.Area == DiffArea.Registry),

            // Folders and files only; a whole drive or the file-system category copies everything changed (F3).
            DiffAction.CopyToFiles => selection.All(i => i.Area == DiffArea.FileSystem),

            // Keys and values; never a whole category.
            DiffAction.RevertValue => nodesOnly && selection.All(i => i.Area == DiffArea.Registry),

            _ => false,
        };
    }

    /// <summary>The comparison nodes a selection stands for: a category stands for its top-level nodes.</summary>
    public static List<int> Nodes(DiffResult diff, IEnumerable<DiffItem> selection, DiffArea area)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selection);
        var result = new List<int>();
        foreach (var item in selection.Where(i => i.Area == area))
        {
            if (IsCategory(item))
            {
                result.AddRange(diff.Tree(area).Roots());
            }
            else
            {
                result.Add(item.Node);
            }
        }

        return result;
    }
}
