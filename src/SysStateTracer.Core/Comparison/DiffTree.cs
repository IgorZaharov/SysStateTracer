namespace SysStateTracer.Core.Comparison;

/// <summary>What a comparison node stands for.</summary>
public enum DiffItemKind : byte
{
    Folder,
    File,
    Key,
    Value,
    Service,
}

/// <summary>How an item differs between the left and right snapshot.</summary>
public enum ChangeKind : byte
{
    /// <summary>Unchanged itself; shown only because something below it changed.</summary>
    Unchanged,

    /// <summary>Only in the right snapshot (green).</summary>
    New,

    /// <summary>Only in the left snapshot (red).</summary>
    Deleted,

    /// <summary>In both, with different fields (yellow).</summary>
    Modified,
}

/// <summary>Which fields of a modified item differ.</summary>
[Flags]
public enum ChangedFields : ushort
{
    None = 0,
    Size = 1 << 0,
    LastWriteTime = 1 << 1,
    Attributes = 1 << 2,
    ValueType = 1 << 3,
    ValueData = 1 << 4,
    DisplayName = 1 << 5,
    ServiceType = 1 << 6,
    State = 1 << 7,
    StartType = 1 << 8,
    ServiceFlags = 1 << 9,
}

/// <summary>
/// The result of comparing one part (file system, registry or services) of two snapshots (plan §9.3).
/// Holds only changed items and the parents needed to reach them, in depth-first order. A new or deleted
/// folder or key is one node standing for its whole subtree (its contents are read from the snapshot that
/// has it). Each node knows its index in the left and right snapshot (-1 where absent), what changed, and
/// how many changed items are inside it. <see cref="Swap"/> exchanges the two sides without recomputing.
/// </summary>
public sealed class DiffTree
{
    private readonly int[] _parent;
    private readonly int[] _descendants;
    private readonly DiffItemKind[] _kind;
    private readonly ChangeKind[] _change;
    private readonly ChangedFields[] _fields;
    private readonly int[] _older;
    private readonly int[] _newer;
    private readonly int[] _changedItems;
    private readonly bool _swapped;

    internal DiffTree(
        int[] parent, int[] descendants, DiffItemKind[] kind, ChangeKind[] change, ChangedFields[] fields,
        int[] older, int[] newer, int[] changedItems, bool swapped = false)
    {
        _parent = parent;
        _descendants = descendants;
        _kind = kind;
        _change = change;
        _fields = fields;
        _older = older;
        _newer = newer;
        _changedItems = changedItems;
        _swapped = swapped;
    }

    public static DiffTree Empty { get; } = new([], [], [], [], [], [], [], []);

    public int Count => _parent.Length;

    /// <summary>True when the newer snapshot is shown on the left.</summary>
    public bool IsSwapped => _swapped;

    /// <summary>Parent node; -1 for a top-level node (a drive, HKLM/HKU, or a service).</summary>
    public int Parent(int node) => _parent[node];

    /// <summary>Nodes below this one (its children follow it directly).</summary>
    public int Descendants(int node) => _descendants[node];

    public DiffItemKind Kind(int node) => _kind[node];

    /// <summary>Change as seen from left to right: after <see cref="Swap"/>, New and Deleted trade places.</summary>
    public ChangeKind Change(int node) => _change[node] switch
    {
        ChangeKind.New when _swapped => ChangeKind.Deleted,
        ChangeKind.Deleted when _swapped => ChangeKind.New,
        var change => change,
    };

    public ChangedFields Fields(int node) => _fields[node];

    /// <summary>Index of the item in the left snapshot's tree (FS entry, registry key or value, service), or -1.</summary>
    public int Left(int node) => _swapped ? _newer[node] : _older[node];

    /// <summary>Index of the item in the right snapshot, or -1.</summary>
    public int Right(int node) => _swapped ? _older[node] : _newer[node];

    /// <summary>Changed items in this node's subtree, itself included (the "(12)" after a folder).</summary>
    public int ChangedItems(int node) => _changedItems[node];

    /// <summary>Top-level nodes, in order.</summary>
    public IEnumerable<int> Roots()
    {
        for (var n = 0; n < _parent.Length; n += _descendants[n] + 1)
        {
            yield return n;
        }
    }

    /// <summary>Direct children of <paramref name="node"/>, in order.</summary>
    public IEnumerable<int> Children(int node)
    {
        var end = node + _descendants[node];
        for (var c = node + 1; c <= end; c += _descendants[c] + 1)
        {
            yield return c;
        }
    }

    /// <summary>The same result with left and right exchanged (the ⇄ button); nothing is recomputed.</summary>
    public DiffTree Swap() => new(_parent, _descendants, _kind, _change, _fields, _older, _newer, _changedItems, !_swapped);
}

/// <summary>Collects nodes in depth-first order while comparing.</summary>
internal sealed class DiffTreeBuilder
{
    private readonly List<int> _parent = [];
    private readonly List<int> _descendants = [];
    private readonly List<DiffItemKind> _kind = [];
    private readonly List<ChangeKind> _change = [];
    private readonly List<ChangedFields> _fields = [];
    private readonly List<int> _older = [];
    private readonly List<int> _newer = [];
    private readonly List<int> _changedItems = [];

    public int Count => _parent.Count;

    /// <summary>Adds a node; <paramref name="changedItems"/> is its own weight (0 for a node that only leads to changes).</summary>
    public int Add(int parent, DiffItemKind kind, ChangeKind change, ChangedFields fields, int older, int newer, int changedItems)
    {
        _parent.Add(parent);
        _descendants.Add(0);
        _kind.Add(kind);
        _change.Add(change);
        _fields.Add(fields);
        _older.Add(older);
        _newer.Add(newer);
        _changedItems.Add(changedItems);
        return _parent.Count - 1;
    }

    /// <summary>
    /// Finishes <paramref name="node"/> once its children are added: sets its subtree size and adds the
    /// children's changes to its own. A node left with no change at all is removed; returns false then.
    /// </summary>
    public bool Close(int node)
    {
        var descendants = _parent.Count - node - 1;
        _descendants[node] = descendants;
        var total = _changedItems[node];
        for (var c = node + 1; c <= node + descendants; c += _descendants[c] + 1)
        {
            total += _changedItems[c];
        }

        if (total == 0 && _change[node] == ChangeKind.Unchanged)
        {
            Truncate(node);
            return false;
        }

        _changedItems[node] = total;
        return true;
    }

    public DiffTree Build() => new(
        [.. _parent], [.. _descendants], [.. _kind], [.. _change], [.. _fields], [.. _older], [.. _newer], [.. _changedItems]);

    private void Truncate(int count)
    {
        var remove = _parent.Count - count;
        _parent.RemoveRange(count, remove);
        _descendants.RemoveRange(count, remove);
        _kind.RemoveRange(count, remove);
        _change.RemoveRange(count, remove);
        _fields.RemoveRange(count, remove);
        _older.RemoveRange(count, remove);
        _newer.RemoveRange(count, remove);
        _changedItems.RemoveRange(count, remove);
    }
}
