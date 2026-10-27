using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Comparison;

/// <summary>Counts for the summary line: items new, deleted and modified (a new folder counts with its contents).</summary>
public readonly record struct DiffCounts(long New, long Deleted, long Modified)
{
    public long Total => New + Deleted + Modified;
}

/// <summary>Two snapshots and how they differ, with the left one shown first (normally the older).</summary>
public sealed class DiffResult
{
    internal DiffResult(Snapshot left, Snapshot right, DiffTree fileSystem, DiffTree registry, DiffTree services, IgnoreMatcher ignore)
    {
        Ignore = ignore;
        Left = left;
        Right = right;
        FileSystem = fileSystem;
        Registry = registry;
        Services = services;
    }

    public Snapshot Left { get; }

    public Snapshot Right { get; }

    /// <summary>The first snapshot passed to the comparison; New/Deleted are relative to it before any swap.</summary>
    public Snapshot Older => FileSystem.IsSwapped ? Right : Left;

    /// <summary>The second snapshot passed to the comparison (exports and reverts use it as "the newer one").</summary>
    public Snapshot Newer => FileSystem.IsSwapped ? Left : Right;

    public DiffTree FileSystem { get; }

    public DiffTree Registry { get; }

    public DiffTree Services { get; }

    /// <summary>
    /// The Global Ignore rules the comparison was made with. A new or deleted folder or key is one node,
    /// so exports apply these rules again to what is inside it.
    /// </summary>
    public IgnoreMatcher Ignore { get; }

    public DiffTree Tree(DiffArea area) => area switch
    {
        DiffArea.FileSystem => FileSystem,
        DiffArea.Registry => Registry,
        _ => Services,
    };

    /// <summary>Right on the left and vice versa; New and Deleted trade places. Instant.</summary>
    public DiffResult Swap() => new(Right, Left, FileSystem.Swap(), Registry.Swap(), Services.Swap(), Ignore);

    public DiffCounts FileSystemCounts => Count(FileSystem, n => n.Kind == DiffItemKind.Folder ? Size(Side(FileSystem, n.Node).FileSystem.Descendants, Index(FileSystem, n.Node)) : 0);

    public DiffCounts RegistryCounts => Count(Registry, n => n.Kind == DiffItemKind.Key ? Size(Side(Registry, n.Node).Registry.Descendants, Index(Registry, n.Node)) : 0);

    public DiffCounts ServicesCounts => Count(Services, _ => 0);

    /// <summary>Name of a node, e.g. "notepad.exe", a value name (empty for the default value) or a service name.</summary>
    public string Name(DiffTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var snapshot = Side(tree, node);
        var index = Index(tree, node);
        var id = tree.Kind(node) switch
        {
            DiffItemKind.Folder or DiffItemKind.File => snapshot.FileSystem.Name[index],
            DiffItemKind.Key => snapshot.Registry.Name[index],
            DiffItemKind.Value => snapshot.Registry.ValueName[index],
            _ => snapshot.Services.Name[index],
        };
        return snapshot.Names.GetString(id);
    }

    /// <summary>Full path, e.g. "C:\Windows\notepad.exe", "HKLM\SOFTWARE\Vendor" or "HKLM\SOFTWARE\Vendor\ValueName".</summary>
    public string FullPath(DiffTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var snapshot = Side(tree, node);
        var index = Index(tree, node);
        return tree.Kind(node) switch
        {
            DiffItemKind.Folder or DiffItemKind.File => snapshot.FileSystem.GetFullPath(index, snapshot.Names),
            DiffItemKind.Key => snapshot.Registry.GetFullPath(index, snapshot.Names),
            DiffItemKind.Value => FullPath(tree, tree.Parent(node)) + "\\" + Name(tree, node),
            _ => Name(tree, node),
        };
    }

    /// <summary>The snapshot that has the node: the left one unless the item exists only on the right.</summary>
    private Snapshot Side(DiffTree tree, int node) => tree.Left(node) >= 0 ? Left : Right;

    private static int Index(DiffTree tree, int node) => tree.Left(node) >= 0 ? tree.Left(node) : tree.Right(node);

    private static int Size(ReadOnlySpan<int> descendants, int index) => descendants[index];

    private static DiffCounts Count(DiffTree tree, Func<(int Node, DiffItemKind Kind), int> blockExtra)
    {
        long added = 0, deleted = 0, modified = 0;
        for (var n = 0; n < tree.Count; n++)
        {
            switch (tree.Change(n))
            {
                case ChangeKind.New:
                    added += 1 + blockExtra((n, tree.Kind(n)));
                    break;
                case ChangeKind.Deleted:
                    deleted += 1 + blockExtra((n, tree.Kind(n)));
                    break;
                case ChangeKind.Modified:
                    modified++;
                    break;
            }
        }

        return new DiffCounts(added, deleted, modified);
    }
}

/// <summary>
/// Compares two snapshots (plan §9.2): a synchronized depth-first walk over both sorted trees. A folder
/// or key whose subtree digest is equal on both sides is skipped in one step, so the cost grows with the
/// number of changes, not the number of items. A folder that is only on one side is recorded as one node.
/// Names are matched ignoring case, as Windows does. Items matching <paramref name="ignore"/> rules are left out.
/// </summary>
public static class SnapshotComparer
{
    public static DiffResult Compare(Snapshot older, Snapshot newer, IgnoreMatcher? ignore = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        ignore ??= IgnoreMatcher.Empty;

        var fs = Task.Run(() => new FileSystemWalk(older, newer, ignore, cancellationToken).Run(), cancellationToken);
        var reg = Task.Run(() => new RegistryWalk(older, newer, ignore, cancellationToken).Run(), cancellationToken);
        var services = CompareServices(older, newer, ignore);
        Task.WhenAll(fs, reg).GetAwaiter().GetResult();
        return new DiffResult(older, newer, fs.Result, reg.Result, services, ignore);
    }

    private static int CompareNames(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => a.CompareTo(b, StringComparison.OrdinalIgnoreCase);

    private static DiffTree CompareServices(Snapshot older, Snapshot newer, IgnoreMatcher ignore)
    {
        var a = older.Services;
        var b = newer.Services;
        var builder = new DiffTreeBuilder();
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            var order = i >= a.Count ? 1 : j >= b.Count ? -1 : CompareNames(older.Names[a.Name[i]], newer.Names[b.Name[j]]);
            if (order < 0)
            {
                if (!ignore.IsIgnoredEntry(IgnoreTarget.Service, older.Names[a.Name[i]]))
                {
                    builder.Add(-1, DiffItemKind.Service, ChangeKind.Deleted, ChangedFields.None, i, -1, 1);
                }

                i++;
            }
            else if (order > 0)
            {
                if (!ignore.IsIgnoredEntry(IgnoreTarget.Service, newer.Names[b.Name[j]]))
                {
                    builder.Add(-1, DiffItemKind.Service, ChangeKind.New, ChangedFields.None, -1, j, 1);
                }

                j++;
            }
            else
            {
                var fields = ChangedFields.None;
                if (!older.Names[a.DisplayName[i]].SequenceEqual(newer.Names[b.DisplayName[j]]))
                {
                    fields |= ChangedFields.DisplayName;
                }

                if (a.ServiceType[i] != b.ServiceType[j])
                {
                    fields |= ChangedFields.ServiceType;
                }

                if (a.State[i] != b.State[j])
                {
                    fields |= ChangedFields.State;
                }

                if (a.StartType[i] != b.StartType[j])
                {
                    fields |= ChangedFields.StartType;
                }

                if (a.Flags[i] != b.Flags[j])
                {
                    fields |= ChangedFields.ServiceFlags;
                }

                if (fields != ChangedFields.None && !ignore.IsIgnoredEntry(IgnoreTarget.Service, newer.Names[b.Name[j]]))
                {
                    builder.Add(-1, DiffItemKind.Service, ChangeKind.Modified, fields, i, j, 1);
                }

                i++;
                j++;
            }
        }

        return builder.Build();
    }

    /// <summary>A path built segment by segment, for ignore-rule checks on changed items only.</summary>
    private sealed class PathBuffer
    {
        private readonly Stack<int> _lengths = new();
        private char[] _chars = new char[512];
        private int _length;

        public ReadOnlySpan<char> Span => _chars.AsSpan(0, _length);

        public void Push(ReadOnlySpan<char> segment, string separator)
        {
            _lengths.Push(_length);
            if (_length > 0)
            {
                Append(separator);
            }

            Append(segment);
        }

        public void Pop() => _length = _lengths.Pop();

        private void Append(ReadOnlySpan<char> text)
        {
            if (_length + text.Length > _chars.Length)
            {
                Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _length + text.Length));
            }

            text.CopyTo(_chars.AsSpan(_length));
            _length += text.Length;
        }
    }

    private sealed class FileSystemWalk(Snapshot older, Snapshot newer, IgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        private readonly FsTree _a = older.FileSystem;
        private readonly FsTree _b = newer.FileSystem;
        private readonly NameTable _an = older.Names;
        private readonly NameTable _bn = newer.Names;
        private readonly DiffTreeBuilder _out = new();
        private readonly PathBuffer _path = new();
        private int _steps;

        public DiffTree Run()
        {
            Merge(-1, -1, -1);
            return _out.Build();
        }

        /// <summary>Merges the children of <paramref name="a"/> and <paramref name="b"/> (-1, -1 = the drives).</summary>
        private void Merge(int a, int b, int parentNode)
        {
            var ca = a + 1;
            var endA = a < 0 ? _a.Count - 1 : a + _a.Descendants[a];
            var cb = b + 1;
            var endB = b < 0 ? _b.Count - 1 : b + _b.Descendants[b];
            while (ca <= endA || cb <= endB)
            {
                if ((++_steps & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var order = ca > endA ? 1 : cb > endB ? -1 : CompareNames(_an[_a.Name[ca]], _bn[_b.Name[cb]]);
                if (order < 0)
                {
                    Block(ca, -1, parentNode);
                    ca += _a.Descendants[ca] + 1;
                }
                else if (order > 0)
                {
                    Block(-1, cb, parentNode);
                    cb += _b.Descendants[cb] + 1;
                }
                else
                {
                    Pair(ca, cb, parentNode);
                    ca += _a.Descendants[ca] + 1;
                    cb += _b.Descendants[cb] + 1;
                }
            }
        }

        /// <summary>An entry only on one side: one node for it and everything below it.</summary>
        private void Block(int x, int y, int parentNode)
        {
            var isOlder = x >= 0;
            var tree = isOlder ? _a : _b;
            var index = isOlder ? x : y;
            var name = isOlder ? _an[tree.Name[index]] : _bn[tree.Name[index]];
            _path.Push(name, "\\");
            try
            {
                if (ignore.RuleCount > 0 && ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, FsPath()))
                {
                    return;
                }

                var kind = tree.IsDirectory(index) ? DiffItemKind.Folder : DiffItemKind.File;
                _out.Add(parentNode, kind, isOlder ? ChangeKind.Deleted : ChangeKind.New, ChangedFields.None, x, y, 1 + tree.Descendants[index]);
            }
            finally
            {
                _path.Pop();
            }
        }

        private void Pair(int x, int y, int parentNode)
        {
            var nameA = _an[_a.Name[x]];
            var nameB = _bn[_b.Name[y]];
            if (_a.Digest[x] == _b.Digest[y] && nameA.SequenceEqual(nameB))
            {
                return; // identical subtree
            }

            var dirA = _a.IsDirectory(x);
            var dirB = _b.IsDirectory(y);
            if (dirA != dirB)
            {
                // A file replaced by a folder of the same name, or the reverse.
                Block(x, -1, parentNode);
                Block(-1, y, parentNode);
                return;
            }

            _path.Push(nameB, "\\");
            try
            {
                if (ignore.RuleCount > 0 && ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, FsPath()))
                {
                    return;
                }

                // A folder's own time changes whenever its contents do, so only its attributes count;
                // its changed contents appear below it.
                var fields = ChangedFields.None;
                if (_a.Attributes[x] != _b.Attributes[y])
                {
                    fields |= ChangedFields.Attributes;
                }

                if (!dirA)
                {
                    if (_a.Size[x] != _b.Size[y])
                    {
                        fields |= ChangedFields.Size;
                    }

                    if (_a.LastWriteTime[x] != _b.LastWriteTime[y])
                    {
                        fields |= ChangedFields.LastWriteTime;
                    }
                }

                var node = _out.Add(
                    parentNode,
                    dirA ? DiffItemKind.Folder : DiffItemKind.File,
                    fields == ChangedFields.None ? ChangeKind.Unchanged : ChangeKind.Modified,
                    fields,
                    x,
                    y,
                    fields == ChangedFields.None ? 0 : 1);
                if (dirA)
                {
                    Merge(x, y, node);
                }

                _out.Close(node);
            }
            finally
            {
                _path.Pop();
            }
        }

        /// <summary>Drive roots are named "C:"; the matcher expects "C:\…" paths, which the buffer already gives below the root.</summary>
        private ReadOnlySpan<char> FsPath() => _path.Span;
    }

    private sealed class RegistryWalk(Snapshot older, Snapshot newer, IgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        private readonly RegTree _a = older.Registry;
        private readonly RegTree _b = newer.Registry;
        private readonly NameTable _an = older.Names;
        private readonly NameTable _bn = newer.Names;
        private readonly DiffTreeBuilder _out = new();
        private readonly PathBuffer _path = new();
        private int _steps;

        public DiffTree Run()
        {
            Merge(-1, -1, -1);
            return _out.Build();
        }

        private void Merge(int a, int b, int parentNode)
        {
            if (a >= 0)
            {
                MergeValues(a, b, parentNode);
            }

            var ca = a + 1;
            var endA = a < 0 ? _a.KeyCount - 1 : a + _a.Descendants[a];
            var cb = b + 1;
            var endB = b < 0 ? _b.KeyCount - 1 : b + _b.Descendants[b];
            while (ca <= endA || cb <= endB)
            {
                if ((++_steps & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var order = ca > endA ? 1 : cb > endB ? -1 : CompareNames(_an[_a.Name[ca]], _bn[_b.Name[cb]]);
                if (order < 0)
                {
                    Block(ca, -1, parentNode);
                    ca += _a.Descendants[ca] + 1;
                }
                else if (order > 0)
                {
                    Block(-1, cb, parentNode);
                    cb += _b.Descendants[cb] + 1;
                }
                else
                {
                    Pair(ca, cb, parentNode);
                    ca += _a.Descendants[ca] + 1;
                    cb += _b.Descendants[cb] + 1;
                }
            }
        }

        private void Block(int x, int y, int parentNode)
        {
            var isOlder = x >= 0;
            var tree = isOlder ? _a : _b;
            var index = isOlder ? x : y;
            _path.Push(isOlder ? _an[tree.Name[index]] : _bn[tree.Name[index]], "\\");
            try
            {
                if (ignore.RuleCount > 0 && ignore.IsIgnoredEntry(IgnoreTarget.Registry, _path.Span))
                {
                    return;
                }

                _out.Add(parentNode, DiffItemKind.Key, isOlder ? ChangeKind.Deleted : ChangeKind.New, ChangedFields.None, x, y, 1 + tree.Descendants[index]);
            }
            finally
            {
                _path.Pop();
            }
        }

        private void Pair(int x, int y, int parentNode)
        {
            var nameB = _bn[_b.Name[y]];
            if (_a.Digest[x] == _b.Digest[y] && _an[_a.Name[x]].SequenceEqual(nameB))
            {
                return;
            }

            _path.Push(nameB, "\\");
            try
            {
                if (ignore.RuleCount > 0 && ignore.IsIgnoredEntry(IgnoreTarget.Registry, _path.Span))
                {
                    return;
                }

                var node = _out.Add(parentNode, DiffItemKind.Key, ChangeKind.Unchanged, ChangedFields.None, x, y, 0);
                Merge(x, y, node);
                _out.Close(node);
            }
            finally
            {
                _path.Pop();
            }
        }

        /// <summary>Values of a key present on both sides, matched by name.</summary>
        private void MergeValues(int x, int y, int keyNode)
        {
            var va = _a.FirstValue[x];
            var endA = va + _a.KeyValueCount[x];
            var vb = _b.FirstValue[y];
            var endB = vb + _b.KeyValueCount[y];
            while (va < endA || vb < endB)
            {
                var order = va >= endA ? 1 : vb >= endB ? -1 : CompareNames(_an[_a.ValueName[va]], _bn[_b.ValueName[vb]]);
                if (order < 0)
                {
                    AddValue(keyNode, ChangeKind.Deleted, ChangedFields.None, va, -1, _an[_a.ValueName[va]]);
                    va++;
                }
                else if (order > 0)
                {
                    AddValue(keyNode, ChangeKind.New, ChangedFields.None, -1, vb, _bn[_b.ValueName[vb]]);
                    vb++;
                }
                else
                {
                    var fields = ChangedFields.None;
                    if (_a.ValueDataType[va] != _b.ValueDataType[vb])
                    {
                        fields |= ChangedFields.ValueType;
                    }

                    if (_a.ValueDataHash[va] != _b.ValueDataHash[vb] || _a.ValueDataLength[va] != _b.ValueDataLength[vb])
                    {
                        fields |= ChangedFields.ValueData;
                    }

                    if (fields != ChangedFields.None)
                    {
                        AddValue(keyNode, ChangeKind.Modified, fields, va, vb, _bn[_b.ValueName[vb]]);
                    }

                    va++;
                    vb++;
                }
            }
        }

        private void AddValue(int keyNode, ChangeKind change, ChangedFields fields, int older, int newer, ReadOnlySpan<char> name)
        {
            if (ignore.RuleCount > 0)
            {
                _path.Push(name, "\\");
                var ignored = ignore.IsIgnoredEntry(IgnoreTarget.Registry, _path.Span);
                _path.Pop();
                if (ignored)
                {
                    return;
                }
            }

            _out.Add(keyNode, DiffItemKind.Value, change, fields, older, newer, 1);
        }
    }
}
