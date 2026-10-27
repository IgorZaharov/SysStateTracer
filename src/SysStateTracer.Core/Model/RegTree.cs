using System.Buffers.Binary;
using System.IO.Hashing;

namespace SysStateTracer.Core.Model;

/// <summary>
/// Registry part of a snapshot: roots such as "HKLM" and "HKU", keys in depth-first pre-order with
/// sorted siblings, and each key's values stored contiguously and sorted by name.
/// Value data is de-duplicated in a <see cref="BlobStore"/>; values larger than
/// <see cref="RegTreeBuilder.MaxStoredValueSize"/> keep only their hash and length.
/// </summary>
public sealed class RegTree
{
    private readonly int[] _parent;
    private readonly int[] _name;
    private readonly long[] _lastWrite;
    private readonly int[] _firstValue;
    private readonly int[] _valueCount;
    private readonly int[] _descendants;
    private readonly ulong[] _digest;

    private readonly int[] _valueName;
    private readonly uint[] _valueType;
    private readonly ulong[] _valueHash;
    private readonly int[] _valueBlob;
    private readonly int[] _valueLength;

    internal RegTree(
        int[] parent, int[] name, long[] lastWrite, int[] firstValue, int[] valueCount, int[] descendants, ulong[] digest,
        int[] valueName, uint[] valueType, ulong[] valueHash, int[] valueBlob, int[] valueLength,
        BlobStore blobs)
    {
        var keys = parent.Length;
        if (name.Length != keys || lastWrite.Length != keys || firstValue.Length != keys ||
            valueCount.Length != keys || descendants.Length != keys || digest.Length != keys)
        {
            throw new ArgumentException("Registry key columns have different lengths.");
        }

        var values = valueName.Length;
        if (valueType.Length != values || valueHash.Length != values || valueBlob.Length != values || valueLength.Length != values)
        {
            throw new ArgumentException("Registry value columns have different lengths.");
        }

        _parent = parent;
        _name = name;
        _lastWrite = lastWrite;
        _firstValue = firstValue;
        _valueCount = valueCount;
        _descendants = descendants;
        _digest = digest;
        _valueName = valueName;
        _valueType = valueType;
        _valueHash = valueHash;
        _valueBlob = valueBlob;
        _valueLength = valueLength;
        Blobs = blobs;
    }

    public static RegTree Empty { get; } = new([], [], [], [], [], [], [], [], [], [], [], [], BlobStore.Empty);

    public int KeyCount => _parent.Length;

    public int ValueCount => _valueName.Length;

    // ---- Keys ----

    /// <summary>Parent key; -1 for a root such as "HKLM".</summary>
    public ReadOnlySpan<int> Parent => _parent;

    public ReadOnlySpan<int> Name => _name;

    /// <summary>Key last-write time (FILETIME, UTC).</summary>
    public ReadOnlySpan<long> LastWriteTime => _lastWrite;

    /// <summary>Index of the key's first value in the value columns.</summary>
    public ReadOnlySpan<int> FirstValue => _firstValue;

    /// <summary>Number of values directly in the key.</summary>
    public ReadOnlySpan<int> KeyValueCount => _valueCount;

    public ReadOnlySpan<int> Descendants => _descendants;

    /// <summary>64-bit hash of the key's name, its values and everything below it (not its timestamp).</summary>
    public ReadOnlySpan<ulong> Digest => _digest;

    // ---- Values ----

    /// <summary>Value name id; the default value has the empty name.</summary>
    public ReadOnlySpan<int> ValueName => _valueName;

    /// <summary>Registry type: 1 = REG_SZ, 2 = REG_EXPAND_SZ, 3 = REG_BINARY, 4 = REG_DWORD, 7 = REG_MULTI_SZ, 11 = REG_QWORD, ...</summary>
    public ReadOnlySpan<uint> ValueDataType => _valueType;

    /// <summary>XxHash3 of the value data.</summary>
    public ReadOnlySpan<ulong> ValueDataHash => _valueHash;

    /// <summary>Data length in bytes.</summary>
    public ReadOnlySpan<int> ValueDataLength => _valueLength;

    /// <summary>Blob id holding the data, or -1 when the data was too large to store.</summary>
    public ReadOnlySpan<int> ValueBlob => _valueBlob;

    /// <summary>Value data; empty when not stored or when blobs were not loaded.</summary>
    public BlobStore Blobs { get; }

    public bool IsValueDataStored(int value) => _valueBlob[value] >= 0;

    public ReadOnlySpan<byte> GetValueData(int value)
    {
        var blob = _valueBlob[value];
        return blob >= 0 && blob < Blobs.Count ? Blobs[blob] : [];
    }

    /// <summary>Full key path, e.g. <c>HKLM\SOFTWARE\Microsoft</c>.</summary>
    public string GetFullPath(int key, NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return TreePaths.Build(_parent, _name, key, names, rootOnlySuffix: string.Empty);
    }
}

/// <summary>De-duplicated registry value data.</summary>
public sealed class BlobStore
{
    private readonly byte[] _data;
    private readonly long[] _offsets;

    internal BlobStore(byte[] data, long[] offsets)
    {
        if (offsets.Length == 0 || offsets[0] != 0 || offsets[^1] > data.Length)
        {
            throw new ArgumentException("Blob offsets do not match the data.", nameof(offsets));
        }

        _data = data;
        _offsets = offsets;
    }

    public static BlobStore Empty { get; } = new([], [0]);

    public int Count => _offsets.Length - 1;

    public ReadOnlySpan<byte> this[int id]
    {
        get
        {
            var start = _offsets[id];
            return _data.AsSpan(checked((int)start), checked((int)(_offsets[id + 1] - start)));
        }
    }

    internal ReadOnlySpan<byte> Data => _data;

    internal ReadOnlySpan<long> Offsets => _offsets;
}

/// <summary>
/// Collects registry keys (parents before children) and their values, then builds a <see cref="RegTree"/>.
/// Not thread-safe.
/// </summary>
public sealed class RegTreeBuilder
{
    /// <summary>Values larger than this keep only hash and length (plan §7.7).</summary>
    public const int MaxStoredValueSize = 1024 * 1024;

    private readonly NameTable _names;

    private int[] _parent = new int[1024];
    private int[] _name = new int[1024];
    private long[] _lastWrite = new long[1024];
    private int _keyCount;

    private int[] _vKey = new int[4096];
    private int[] _vName = new int[4096];
    private uint[] _vType = new uint[4096];
    private ulong[] _vHash = new ulong[4096];
    private int[] _vBlob = new int[4096];
    private int[] _vLength = new int[4096];
    private int _valueCount;

    // Blob data goes into fixed-size chunks (no copying as it grows); a blob never spans two chunks.
    // Offsets are positions in the final, contiguous data that Build() produces.
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly List<(byte[] Data, int Used)> _chunks = [];
    private long _blobLength;
    private long[] _blobOffsets = new long[1024 + 1];
    private long[] _blobLocation = new long[1024];
    private int _blobCount;
    private readonly Dictionary<ulong, int> _blobByHash = new();

    public RegTreeBuilder(NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names;
    }

    public int KeyCount => _keyCount;

    public int ValueCount => _valueCount;

    /// <summary>Adds a root such as "HKLM" or "HKU".</summary>
    public int AddRoot(ReadOnlySpan<char> name, long lastWriteTime) => AddKey(-1, name, lastWriteTime);

    public int AddKey(int parent, ReadOnlySpan<char> name, long lastWriteTime)
    {
        if (parent < -1 || parent >= _keyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(parent), parent, "Parent key must be added before its sub-keys.");
        }

        if (_keyCount == _parent.Length)
        {
            var capacity = _parent.Length * 2;
            Array.Resize(ref _parent, capacity);
            Array.Resize(ref _name, capacity);
            Array.Resize(ref _lastWrite, capacity);
        }

        var k = _keyCount++;
        _parent[k] = parent;
        _name[k] = _names.Add(name);
        _lastWrite[k] = lastWriteTime;
        return k;
    }

    public void AddValue(int key, ReadOnlySpan<char> name, uint type, ReadOnlySpan<byte> data)
    {
        if ((uint)key >= (uint)_keyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (_valueCount == _vKey.Length)
        {
            var capacity = _vKey.Length * 2;
            Array.Resize(ref _vKey, capacity);
            Array.Resize(ref _vName, capacity);
            Array.Resize(ref _vType, capacity);
            Array.Resize(ref _vHash, capacity);
            Array.Resize(ref _vBlob, capacity);
            Array.Resize(ref _vLength, capacity);
        }

        var hash = XxHash3.HashToUInt64(data);
        var v = _valueCount++;
        _vKey[v] = key;
        _vName[v] = _names.Add(name);
        _vType[v] = type;
        _vHash[v] = hash;
        _vLength[v] = data.Length;
        _vBlob[v] = data.Length > MaxStoredValueSize ? -1 : StoreBlob(hash, data);
    }

    /// <summary>
    /// Adds a value whose hash and length are already known (taken from a previous snapshot).
    /// <paramref name="data"/> is used only when <paramref name="stored"/> is true.
    /// </summary>
    internal void AddKnownValue(int key, ReadOnlySpan<char> name, uint type, ulong hash, int length, ReadOnlySpan<byte> data, bool stored)
    {
        if ((uint)key >= (uint)_keyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (_valueCount == _vKey.Length)
        {
            var capacity = _vKey.Length * 2;
            Array.Resize(ref _vKey, capacity);
            Array.Resize(ref _vName, capacity);
            Array.Resize(ref _vType, capacity);
            Array.Resize(ref _vHash, capacity);
            Array.Resize(ref _vBlob, capacity);
            Array.Resize(ref _vLength, capacity);
        }

        var v = _valueCount++;
        _vKey[v] = key;
        _vName[v] = _names.Add(name);
        _vType[v] = type;
        _vHash[v] = hash;
        _vLength[v] = length;
        _vBlob[v] = stored ? StoreBlob(hash, data) : -1;
    }

    /// <summary>Builds the tree and empties the builder (its memory is released).</summary>
    public RegTree Build()
    {
        var keys = _keyCount;
        var layout = TreeLayout.PreOrder(keys, _parent, _name, _names);
        var order = layout.Order;
        var newIndex = new int[keys];
        var name = new int[keys];
        var lastWrite = new long[keys];
        for (var i = 0; i < keys; i++)
        {
            var old = order[i];
            newIndex[old] = i;
            name[i] = _name[old];
            lastWrite[i] = _lastWrite[old];
        }

        // Group values by their key's new index, then sort each key's values by name.
        var values = _valueCount;
        var valueCount = new int[keys];
        for (var v = 0; v < values; v++)
        {
            valueCount[newIndex[_vKey[v]]]++;
        }

        var firstValue = new int[keys];
        for (int k = 0, sum = 0; k < keys; k++)
        {
            firstValue[k] = sum;
            sum += valueCount[k];
        }

        var placed = new int[values];
        var fill = (int[])firstValue.Clone();
        for (var v = 0; v < values; v++)
        {
            placed[fill[newIndex[_vKey[v]]]++] = v;
        }

        var comparer = new ValueComparer(_vName, _names);
        for (var k = 0; k < keys; k++)
        {
            if (valueCount[k] > 1)
            {
                Array.Sort(placed, firstValue[k], valueCount[k], comparer);
            }
        }

        var valueName = new int[values];
        var valueType = new uint[values];
        var valueHash = new ulong[values];
        var valueBlob = new int[values];
        var valueLength = new int[values];
        for (var i = 0; i < values; i++)
        {
            var v = placed[i];
            valueName[i] = _vName[v];
            valueType[i] = _vType[v];
            valueHash[i] = _vHash[v];
            valueBlob[i] = _vBlob[v];
            valueLength[i] = _vLength[v];
        }

        var digest = ComputeDigests(layout.Descendants, name, firstValue, valueCount, valueName, valueType, valueHash, valueLength);

        var blobData = GC.AllocateUninitializedArray<byte>(checked((int)_blobLength));
        var position = 0;
        foreach (var (data, used) in _chunks)
        {
            data.AsSpan(0, used).CopyTo(blobData.AsSpan(position));
            position += used;
        }

        var blobOffsets = _blobOffsets.AsSpan(0, _blobCount + 1).ToArray();
        Reset();

        return new RegTree(
            layout.Parent, name, lastWrite, firstValue, valueCount, layout.Descendants, digest,
            valueName, valueType, valueHash, valueBlob, valueLength,
            new BlobStore(blobData, blobOffsets));
    }

    private void Reset()
    {
        _parent = new int[16];
        _name = new int[16];
        _lastWrite = new long[16];
        _keyCount = 0;
        _vKey = new int[16];
        _vName = new int[16];
        _vType = new uint[16];
        _vHash = new ulong[16];
        _vBlob = new int[16];
        _vLength = new int[16];
        _valueCount = 0;
        _chunks.Clear();
        _chunks.TrimExcess();
        _blobLength = 0;
        _blobOffsets = new long[16 + 1];
        _blobLocation = new long[16];
        _blobCount = 0;
        _blobByHash.Clear();
        _blobByHash.TrimExcess();
    }

    private int StoreBlob(ulong hash, ReadOnlySpan<byte> data)
    {
        if (_blobByHash.TryGetValue(hash, out var existing) && Blob(existing).SequenceEqual(data))
        {
            return existing;
        }

        if (_chunks.Count == 0 || _chunks[^1].Used + data.Length > _chunks[^1].Data.Length)
        {
            _chunks.Add((new byte[Math.Max(ChunkSize, data.Length)], 0));
        }

        if (_blobCount + 2 > _blobOffsets.Length)
        {
            Array.Resize(ref _blobOffsets, _blobOffsets.Length * 2);
            Array.Resize(ref _blobLocation, _blobOffsets.Length);
        }

        var chunk = _chunks.Count - 1;
        var (bytes, used) = _chunks[chunk];
        data.CopyTo(bytes.AsSpan(used));
        _chunks[chunk] = (bytes, used + data.Length);

        var id = _blobCount++;
        _blobLocation[id] = ((long)chunk << 32) | (uint)used;
        _blobLength += data.Length;
        _blobOffsets[_blobCount] = _blobLength;
        _blobByHash.TryAdd(hash, id);
        return id;
    }

    private ReadOnlySpan<byte> Blob(int id)
    {
        var location = _blobLocation[id];
        var length = (int)(_blobOffsets[id + 1] - _blobOffsets[id]);
        return _chunks[(int)(location >> 32)].Data.AsSpan((int)(uint)location, length);
    }

    private ulong[] ComputeDigests(
        int[] descendants, int[] name, int[] firstValue, int[] valueCount,
        int[] valueName, uint[] valueType, ulong[] valueHash, int[] valueLength)
    {
        var keys = descendants.Length;
        var digest = new ulong[keys];
        var hasher = new XxHash3();
        Span<byte> fields = stackalloc byte[16];
        Span<byte> child = stackalloc byte[8];

        for (var k = keys - 1; k >= 0; k--)
        {
            hasher.Reset();
            Digests.AppendName(hasher, _names[name[k]]);

            var end = firstValue[k] + valueCount[k];
            for (var v = firstValue[k]; v < end; v++)
            {
                Digests.AppendName(hasher, _names[valueName[v]]);
                BinaryPrimitives.WriteUInt32LittleEndian(fields, valueType[v]);
                BinaryPrimitives.WriteUInt64LittleEndian(fields[4..], valueHash[v]);
                BinaryPrimitives.WriteInt32LittleEndian(fields[12..], valueLength[v]);
                hasher.Append(fields);
            }

            var last = k + descendants[k];
            for (var c = k + 1; c <= last; c += descendants[c] + 1)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(child, digest[c]);
                hasher.Append(child);
            }

            digest[k] = hasher.GetCurrentHashAsUInt64();
        }

        return digest;
    }

    private sealed class ValueComparer(int[] valueName, NameTable names) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var result = NameTable.Compare(names[valueName[x]], names[valueName[y]]);
            return result != 0 ? result : x.CompareTo(y);
        }
    }
}
