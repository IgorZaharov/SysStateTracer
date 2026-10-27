using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Model;

/// <summary>
/// Every distinct name in a snapshot (folder, file, key, value, service names), stored once
/// in one character buffer. Entries refer to names by integer id.
/// While building, <see cref="Add"/> interns names without allocating for names already present.
/// After <see cref="Freeze"/> (or when loaded from a file) the table is read-only.
/// Not thread-safe while building.
/// </summary>
public sealed class NameTable
{
    private char[] _chars;
    private int _charCount;
    private int[] _offsets; // name i spans [_offsets[i], _offsets[i + 1])
    private int _count;

    // Open-addressing hash index over the ids (slot = id + 1, 0 = empty). It refers to names in
    // _chars, so interning allocates no strings. Dropped by Freeze().
    private int[]? _slots;
    private int[] _hashes; // hash of name i

    public NameTable(int capacity = 1024)
    {
        capacity = Math.Max(capacity, 16);
        _chars = new char[capacity * 16];
        _offsets = new int[capacity + 1];
        _hashes = new int[capacity];
        _slots = new int[(int)BitOperations.RoundUpToPowerOf2((uint)capacity * 2)];
    }

    /// <summary>Frozen table from loaded data: <paramref name="offsets"/> has Count + 1 entries.</summary>
    internal NameTable(char[] chars, int[] offsets)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Length == 0 || offsets[0] != 0 || offsets[^1] > chars.Length)
        {
            throw new ArgumentException("Name offsets do not match the character data.", nameof(offsets));
        }

        _chars = chars;
        _charCount = offsets[^1];
        _offsets = offsets;
        _count = offsets.Length - 1;
        _hashes = [];
    }

    /// <summary>Number of distinct names.</summary>
    public int Count => _count;

    /// <summary>True when no more names can be added.</summary>
    public bool IsFrozen => _slots is null;

    /// <summary>The name with id <paramref name="id"/>.</summary>
    public ReadOnlySpan<char> this[int id]
    {
        get
        {
            var start = _offsets[id];
            return _chars.AsSpan(start, _offsets[id + 1] - start);
        }
    }

    /// <summary>All characters of all names, in id order.</summary>
    internal ReadOnlySpan<char> Chars => _chars.AsSpan(0, _charCount);

    /// <summary>Count + 1 offsets into <see cref="Chars"/>.</summary>
    internal ReadOnlySpan<int> Offsets => _offsets.AsSpan(0, _count + 1);

    public string GetString(int id) => new(this[id]);

    /// <summary>Id of <paramref name="name"/>, adding it if new. Names are case-sensitive here.</summary>
    public int Add(ReadOnlySpan<char> name)
    {
        if (_slots is null)
        {
            throw new InvalidOperationException("The name table is frozen.");
        }

        var hash = Hash(name);
        var mask = _slots.Length - 1;
        var slot = hash & mask;
        while (_slots[slot] != 0)
        {
            var candidate = _slots[slot] - 1;
            if (_hashes[candidate] == hash && this[candidate].SequenceEqual(name))
            {
                return candidate;
            }

            slot = (slot + 1) & mask;
        }

        EnsureCapacity(name.Length);
        var id = _count;
        name.CopyTo(_chars.AsSpan(_charCount));
        _charCount += name.Length;
        _count++;
        _offsets[_count] = _charCount;
        _hashes[id] = hash;
        _slots[slot] = id + 1;

        if (_count * 2 > _slots.Length)
        {
            Rehash(_slots.Length * 2);
        }

        return id;
    }

    /// <summary>Drops the lookup index and trims the buffers; the table becomes read-only.</summary>
    public void Freeze()
    {
        if (_slots is null)
        {
            return;
        }

        _slots = null;
        _hashes = [];
        if (_chars.Length != _charCount)
        {
            Array.Resize(ref _chars, _charCount);
        }

        if (_offsets.Length != _count + 1)
        {
            Array.Resize(ref _offsets, _count + 1);
        }
    }

    /// <summary>
    /// Snapshot ordering of names: case-insensitive ordinal (Windows semantics), then
    /// case-sensitive ordinal so the order is always deterministic.
    /// </summary>
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var result = a.CompareTo(b, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : a.SequenceCompareTo(b);
    }

    private void EnsureCapacity(int extraChars)
    {
        if (_charCount + extraChars > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _charCount + extraChars));
        }

        if (_count + 2 > _offsets.Length)
        {
            Array.Resize(ref _offsets, _offsets.Length * 2);
        }

        if (_count + 1 > _hashes.Length)
        {
            Array.Resize(ref _hashes, _hashes.Length * 2);
        }
    }

    private static int Hash(ReadOnlySpan<char> name) =>
        (int)XxHash3.HashToUInt64(MemoryMarshal.AsBytes(name)) & int.MaxValue;

    private void Rehash(int size)
    {
        var slots = new int[size];
        var mask = size - 1;
        for (var id = 0; id < _count; id++)
        {
            var slot = _hashes[id] & mask;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            slots[slot] = id + 1;
        }

        _slots = slots;
    }
}
