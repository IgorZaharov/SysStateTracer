using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace SysStateTracer.Core.Model;

/// <summary>
/// File-system part of a snapshot: one root per drive (named e.g. "C:"), stored in depth-first
/// pre-order with siblings sorted by name. Every per-entry value is a column array.
/// Times are FILETIME values (100 ns ticks since 1601-01-01 UTC).
/// </summary>
public sealed class FsTree
{
    public const uint DirectoryAttribute = 0x10;

    private readonly int[] _parent;
    private readonly int[] _name;
    private readonly uint[] _attributes;
    private readonly long[] _size;
    private readonly long[] _lastWrite;
    private readonly int[] _descendants;
    private readonly int[] _childFolders;
    private readonly int[] _childFiles;
    private readonly ulong[] _digest;
    private readonly long[] _fileId;

    internal FsTree(
        int[] parent, int[] name, uint[] attributes, long[] size, long[] lastWrite,
        int[] descendants, int[] childFolders, int[] childFiles, ulong[] digest, long[]? fileId = null)
    {
        var n = parent.Length;
        if (name.Length != n || attributes.Length != n || size.Length != n || lastWrite.Length != n ||
            descendants.Length != n || childFolders.Length != n || childFiles.Length != n || digest.Length != n)
        {
            throw new ArgumentException("File-system columns have different lengths.");
        }

        fileId ??= [];
        if (fileId.Length != 0 && fileId.Length != n)
        {
            throw new ArgumentException("File ids do not match the entries.");
        }

        _parent = parent;
        _name = name;
        _attributes = attributes;
        _size = size;
        _lastWrite = lastWrite;
        _descendants = descendants;
        _childFolders = childFolders;
        _childFiles = childFiles;
        _digest = digest;
        _fileId = fileId;
    }

    public static FsTree Empty { get; } = new([], [], [], [], [], [], [], [], []);

    public int Count => _parent.Length;

    /// <summary>Parent entry; -1 for a drive root.</summary>
    public ReadOnlySpan<int> Parent => _parent;

    public ReadOnlySpan<int> Name => _name;

    public ReadOnlySpan<uint> Attributes => _attributes;

    /// <summary>File size in bytes; 0 for folders.</summary>
    public ReadOnlySpan<long> Size => _size;

    public ReadOnlySpan<long> LastWriteTime => _lastWrite;

    /// <summary>Number of entries below each entry (its subtree is the next Descendants[i] entries).</summary>
    public ReadOnlySpan<int> Descendants => _descendants;

    /// <summary>Direct sub-folders of each folder.</summary>
    public ReadOnlySpan<int> ChildFolders => _childFolders;

    /// <summary>Direct files of each folder.</summary>
    public ReadOnlySpan<int> ChildFiles => _childFiles;

    /// <summary>64-bit hash of the entry and everything below it.</summary>
    public ReadOnlySpan<ulong> Digest => _digest;

    /// <summary>
    /// NTFS file reference number of each entry (0 = unknown). Kept only in SysStateTracer.cache, where it
    /// maps change-journal records to entries (plan §7.8); empty for trees loaded from .stsnap files.
    /// </summary>
    public ReadOnlySpan<long> FileId => _fileId;

    public bool HasFileIds => _fileId.Length == _parent.Length && _parent.Length > 0;

    public bool IsDirectory(int entry) => (_attributes[entry] & DirectoryAttribute) != 0;

    /// <summary>Full path, e.g. <c>C:\Windows\notepad.exe</c>.</summary>
    public string GetFullPath(int entry, NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return TreePaths.Build(_parent, _name, entry, names, rootOnlySuffix: "\\");
    }
}

/// <summary>
/// Collects file-system entries in any order (parents before children), then builds an <see cref="FsTree"/>.
/// Not thread-safe.
/// </summary>
public sealed class FsTreeBuilder
{
    private readonly NameTable _names;
    private int[] _parent;
    private int[] _name;
    private uint[] _attributes;
    private long[] _size;
    private long[] _lastWrite;
    private long[] _fileId;
    private int _count;

    public FsTreeBuilder(NameTable names, int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names;
        capacity = Math.Max(capacity, 16);
        _parent = new int[capacity];
        _name = new int[capacity];
        _attributes = new uint[capacity];
        _size = new long[capacity];
        _lastWrite = new long[capacity];
        _fileId = new long[capacity];
    }

    public int Count => _count;

    /// <summary>Adds a drive root such as "C:".</summary>
    public int AddRoot(ReadOnlySpan<char> name, uint attributes, long lastWriteTime, long fileId = 0) =>
        Add(-1, name, attributes | FsTree.DirectoryAttribute, 0, lastWriteTime, fileId);

    /// <summary>Adds an entry below <paramref name="parent"/> (an index returned earlier).</summary>
    /// <param name="fileId">NTFS file reference number, or 0 when unknown.</param>
    public int Add(int parent, ReadOnlySpan<char> name, uint attributes, long size, long lastWriteTime, long fileId = 0)
    {
        if (parent < -1 || parent >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(parent), parent, "Parent must be added before its children.");
        }

        if (_count == _parent.Length)
        {
            Grow();
        }

        var i = _count++;
        _parent[i] = parent;
        _name[i] = _names.Add(name);
        _attributes[i] = attributes;
        _size[i] = (attributes & FsTree.DirectoryAttribute) != 0 ? 0 : size;
        _lastWrite[i] = lastWriteTime;
        _fileId[i] = fileId;
        return i;
    }

    /// <summary>Builds the tree and empties the builder (its memory is released).</summary>
    public FsTree Build()
    {
        var n = _count;
        var layout = TreeLayout.PreOrder(n, _parent, _name, _names);
        var order = layout.Order;

        var name = new int[n];
        var attributes = new uint[n];
        var size = new long[n];
        var lastWrite = new long[n];
        var fileId = new long[n];
        var anyFileId = false;
        for (var i = 0; i < n; i++)
        {
            var old = order[i];
            name[i] = _name[old];
            attributes[i] = _attributes[old];
            size[i] = _size[old];
            lastWrite[i] = _lastWrite[old];
            fileId[i] = _fileId[old];
            anyFileId |= fileId[i] != 0;
        }

        // The builder's columns are copied; free them before the remaining allocations.
        Reset();

        var childFolders = new int[n];
        var childFiles = new int[n];
        for (var i = 0; i < n; i++)
        {
            var p = layout.Parent[i];
            if (p < 0)
            {
                continue;
            }

            if ((attributes[i] & FsTree.DirectoryAttribute) != 0)
            {
                childFolders[p]++;
            }
            else
            {
                childFiles[p]++;
            }
        }

        var digest = ComputeDigests(layout.Descendants, name, attributes, size, lastWrite, _names);
        return new FsTree(
            layout.Parent, name, attributes, size, lastWrite, layout.Descendants, childFolders, childFiles, digest, anyFileId ? fileId : null);
    }

    private void Reset()
    {
        _count = 0;
        _parent = new int[16];
        _name = new int[16];
        _attributes = new uint[16];
        _size = new long[16];
        _lastWrite = new long[16];
        _fileId = new long[16];
    }

    private static ulong[] ComputeDigests(
        int[] descendants, int[] name, uint[] attributes, long[] size, long[] lastWrite, NameTable names)
    {
        var n = descendants.Length;
        var digest = new ulong[n];
        var hasher = new XxHash3();
        Span<byte> fields = stackalloc byte[20];
        Span<byte> child = stackalloc byte[8];

        // Children have higher indices than their parent, so walking backwards finishes them first.
        for (var i = n - 1; i >= 0; i--)
        {
            hasher.Reset();
            Digests.AppendName(hasher, names[name[i]]);
            BinaryPrimitives.WriteUInt32LittleEndian(fields, attributes[i]);
            BinaryPrimitives.WriteInt64LittleEndian(fields[4..], size[i]);
            BinaryPrimitives.WriteInt64LittleEndian(fields[12..], lastWrite[i]);
            hasher.Append(fields);

            var end = i + descendants[i];
            for (var c = i + 1; c <= end; c += descendants[c] + 1)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(child, digest[c]);
                hasher.Append(child);
            }

            digest[i] = hasher.GetCurrentHashAsUInt64();
        }

        return digest;
    }

    private void Grow()
    {
        var capacity = _parent.Length * 2;
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _name, capacity);
        Array.Resize(ref _attributes, capacity);
        Array.Resize(ref _size, capacity);
        Array.Resize(ref _lastWrite, capacity);
        Array.Resize(ref _fileId, capacity);
    }
}

/// <summary>Path building shared by the file-system and registry trees.</summary>
internal static class TreePaths
{
    /// <summary>
    /// Joins names from the root down with '\'. A root on its own gets <paramref name="rootOnlySuffix"/>
    /// ("C:" → "C:\" for drives, nothing for registry roots such as "HKLM").
    /// </summary>
    public static string Build(ReadOnlySpan<int> parent, ReadOnlySpan<int> name, int entry, NameTable names, string rootOnlySuffix)
    {
        if ((uint)entry >= (uint)parent.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(entry));
        }

        var chain = new List<int>();
        for (var e = entry; e >= 0; e = parent[e])
        {
            chain.Add(e);
        }

        var builder = new StringBuilder();
        builder.Append(names[name[chain[^1]]]);
        if (chain.Count == 1)
        {
            return builder.Append(rootOnlySuffix).ToString();
        }

        for (var d = chain.Count - 2; d >= 0; d--)
        {
            builder.Append('\\').Append(names[name[chain[d]]]);
        }

        return builder.ToString();
    }
}
