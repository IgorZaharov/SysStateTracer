using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs.Ntfs;

/// <summary>
/// The folder tree of one NTFS volume, read straight from its master file table (decision D10).
/// The table is read front to back in large blocks, so the cost is a few sequential reads instead of
/// one or more random reads per folder. Needs administrator rights (the volume is opened directly).
/// Holds, per file record: in-use and directory flags, attributes, last-write time, data size, and
/// every name (hard link) with its parent folder.
/// </summary>
internal sealed class MftIndex
{
    /// <summary>Record number of the volume's root folder.</summary>
    public const int RootRecord = 5;

    /// <summary>Records below this are NTFS metadata files ($MFT, $LogFile, …), never listed.</summary>
    private const int FirstUserRecord = 16;

    private const int ChunkSize = 4 * 1024 * 1024;
    private const int BufferCount = 3;

    private const uint FileSignature = 0x454C4946; // "FILE"
    private const uint AttributeStandardInformation = 0x10;
    private const uint AttributeAttributeList = 0x20;
    private const uint AttributeFileName = 0x30;
    private const uint AttributeData = 0x80;
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const byte NamespaceDos = 2;

    private const byte FlagInUse = 1;
    private const byte FlagDirectory = 2;

    // Attribute bits that are NTFS bookkeeping, not file attributes.
    private const uint DupFileNameIndexPresent = 0x10000000;
    private const uint DupViewIndexPresent = 0x20000000;
    private const uint FileAttributeNormal = 0x80;

    private readonly int _recordCount;
    private readonly byte[] _flags;
    private readonly ushort[] _sequence;
    private readonly uint[] _attributes;
    private readonly long[] _lastWrite;
    private readonly long[] _size;

    // Names (hard links): record, its sequence number, parent record and sequence, name.
    // Sized from the record count up front (about one name per record, ~24 characters per name)
    // so the arrays rarely need to grow and leave copies behind.
    private int _linkCount;
    private int[] _linkRecord;
    private ushort[] _linkSequence;
    private int[] _linkParent;
    private ushort[] _linkParentSequence;
    private int[] _linkNameStart;
    private byte[] _linkNameLength;
    private char[] _chars;
    private int _charCount;

    // Children of each folder, as link numbers (built after reading).
    private int[] _childStart = [];
    private int[] _childLinks = [];

    private MftIndex(int recordCount)
    {
        _recordCount = recordCount;
        _flags = new byte[recordCount];
        _sequence = new ushort[recordCount];
        _attributes = new uint[recordCount];
        _lastWrite = new long[recordCount];
        _size = new long[recordCount];

        var links = Math.Max(1024, recordCount + (recordCount / 16));
        _linkRecord = new int[links];
        _linkSequence = new ushort[links];
        _linkParent = new int[links];
        _linkParentSequence = new ushort[links];
        _linkNameStart = new int[links];
        _linkNameLength = new byte[links];
        _chars = new char[(int)Math.Min(Array.MaxLength, Math.Max(1L << 16, recordCount * 24L))];
    }

    /// <summary>File records that are in use (files and folders, including NTFS metadata).</summary>
    public long RecordsInUse { get; private set; }

    /// <summary>
    /// Reads the master file table of <paramref name="drive"/>. Returns null with a reason when it cannot be
    /// used (not NTFS, no administrator rights, unusual layout); the caller then lists folders instead.
    /// </summary>
    /// <param name="progress">Called with the number of in-use records read so far.</param>
    public static MftIndex? TryLoad(char drive, Action<long>? progress, CancellationToken cancellationToken, out string? reason)
    {
        using var volume = NativeMethods.CreateFile(
            $@"\\.\{char.ToUpperInvariant(drive)}:",
            NativeMethods.GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            NativeMethods.FileFlagNoBuffering | NativeMethods.FileFlagSequentialScan,
            0);
        if (volume.IsInvalid)
        {
            reason = $"cannot open the volume ({Win32Errors.Describe(Marshal.GetLastPInvokeError())})";
            return null;
        }

        if (!TryGetVolumeData(volume, out var data))
        {
            reason = "not an NTFS volume";
            return null;
        }

        if (data.RecordSize < 512 || data.RecordSize > data.BytesPerCluster || data.BytesPerCluster % data.RecordSize != 0 ||
            data.BytesPerSector == 0 || data.MftValidLength / data.RecordSize > int.MaxValue)
        {
            reason = $"unsupported layout (record {data.RecordSize} B, cluster {data.BytesPerCluster} B)";
            return null;
        }

        var runs = ReadMftRuns(volume, data, out reason);
        if (runs is null)
        {
            return null;
        }

        var index = new MftIndex((int)(data.MftValidLength / data.RecordSize));
        reason = index.Read(volume, data, runs, progress, cancellationToken);
        if (reason is not null)
        {
            return null;
        }

        index.BuildChildren();
        return index;
    }

    /// <summary>Record number of <paramref name="relativePath"/> (e.g. "Users\Public") below the root, or -1.</summary>
    public int Find(ReadOnlySpan<char> relativePath)
    {
        var record = RootRecord;
        foreach (var range in relativePath.Split('\\'))
        {
            var segment = relativePath[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            var found = -1;
            foreach (var link in Children(record))
            {
                if (Name(link).Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    found = _linkRecord[link];
                    break;
                }
            }

            if (found < 0 || !IsDirectory(found))
            {
                return -1;
            }

            record = found;
        }

        return record;
    }

    /// <summary>Link numbers of the names inside folder <paramref name="record"/>.</summary>
    public ReadOnlySpan<int> Children(int record) =>
        record + 1 < _childStart.Length
            ? _childLinks.AsSpan(_childStart[record], _childStart[record + 1] - _childStart[record])
            : [];

    public int LinkRecord(int link) => _linkRecord[link];

    public ReadOnlySpan<char> Name(int link) => _chars.AsSpan(_linkNameStart[link], _linkNameLength[link]);

    public bool IsDirectory(int record) => (_flags[record] & FlagDirectory) != 0;

    /// <summary>Attributes as a folder listing reports them (directory flag added, NORMAL when none).</summary>
    public uint Attributes(int record)
    {
        var attributes = _attributes[record] & ~(DupFileNameIndexPresent | DupViewIndexPresent);
        if (IsDirectory(record))
        {
            attributes |= NativeMethods.FileAttributeDirectory;
        }

        return attributes == 0 ? FileAttributeNormal : attributes;
    }

    public long LastWriteTime(int record) => _lastWrite[record];

    /// <summary>NTFS file reference number: record number plus sequence number in the top 16 bits.</summary>
    public long FileReference(int record) => ((long)_sequence[record] << 48) | (uint)record;

    public long Size(int record) => IsDirectory(record) ? 0 : _size[record];

    private static unsafe bool TryGetVolumeData(SafeFileHandle volume, out VolumeData data)
    {
        Span<byte> output = stackalloc byte[128];
        fixed (byte* p = output)
        {
            if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FsctlGetNtfsVolumeData, null, 0, p, output.Length, out var returned, 0) ||
                returned < 72)
            {
                data = default;
                return false;
            }
        }

        // NTFS_VOLUME_DATA_BUFFER
        data = new VolumeData(
            BytesPerSector: BinaryPrimitives.ReadInt32LittleEndian(output[40..]),
            BytesPerCluster: BinaryPrimitives.ReadInt32LittleEndian(output[44..]),
            RecordSize: BinaryPrimitives.ReadInt32LittleEndian(output[48..]),
            MftValidLength: BinaryPrimitives.ReadInt64LittleEndian(output[56..]),
            MftStartLcn: BinaryPrimitives.ReadInt64LittleEndian(output[64..]));
        return true;
    }

    /// <summary>Where the $MFT file itself lies on disk: the data runs of record 0.</summary>
    private static unsafe List<(long Lcn, long Clusters)>? ReadMftRuns(SafeFileHandle volume, VolumeData data, out string? reason)
    {
        var size = (int)AlignUp(data.BytesPerCluster, data.BytesPerSector);
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            if (!ReadAt(volume, data.MftStartLcn * data.BytesPerCluster, buffer, size))
            {
                reason = $"cannot read the file table ({Win32Errors.Describe(Marshal.GetLastPInvokeError())})";
                return null;
            }

            var record = new Span<byte>(buffer, data.RecordSize);
            if (!ApplyFixups(record))
            {
                reason = "first file table record is damaged";
                return null;
            }

            List<(long, long)>? runs = null;
            foreach (var attribute in Attributes(record))
            {
                if (attribute.Type == AttributeAttributeList)
                {
                    reason = "the file table is split over several records";
                    return null;
                }

                if (attribute.Type == AttributeData && attribute.Data[8] != 0 && attribute.Data[9] == 0)
                {
                    runs = ParseRuns(attribute.Data);
                }
            }

            reason = runs is { Count: > 0 } ? null : "file table location not found";
            return reason is null ? runs : null;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>Decodes the mapping pairs of a non-resident attribute: (start cluster, cluster count) per run.</summary>
    private static List<(long Lcn, long Clusters)> ParseRuns(ReadOnlySpan<byte> attribute)
    {
        var runs = new List<(long, long)>();
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
        long lcn = 0;
        while (pos < attribute.Length && attribute[pos] != 0)
        {
            var header = attribute[pos];
            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;
            pos++;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || pos + lengthBytes + offsetBytes > attribute.Length)
            {
                break;
            }

            long clusters = 0;
            for (var i = lengthBytes - 1; i >= 0; i--)
            {
                clusters = (clusters << 8) | attribute[pos + i];
            }

            pos += lengthBytes;
            if (offsetBytes == 0)
            {
                // Sparse run: no clusters on disk. Not expected in $MFT.
                runs.Add((-1, clusters));
                continue;
            }

            long delta = (sbyte)attribute[pos + offsetBytes - 1]; // sign-extended top byte
            for (var i = offsetBytes - 2; i >= 0; i--)
            {
                delta = (delta << 8) | attribute[pos + i];
            }

            pos += offsetBytes;
            lcn += delta;
            runs.Add((lcn, clusters));
        }

        return runs;
    }

    /// <summary>Reads all records: one thread reads blocks from disk while this one parses the previous block.</summary>
    private unsafe string? Read(
        SafeFileHandle volume, VolumeData data, List<(long Lcn, long Clusters)> runs, Action<long>? progress, CancellationToken cancellationToken)
    {
        // Split the runs into blocks of at most ChunkSize, up to the valid length of the table.
        var chunks = new List<(long DiskOffset, int Length, long FirstRecord)>();
        long virtualOffset = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var runBytes = clusters * data.BytesPerCluster;
            for (long done = 0; done < runBytes && virtualOffset < data.MftValidLength;)
            {
                var length = (int)Math.Min(ChunkSize, runBytes - done);
                length = (int)Math.Min(length, AlignUp(data.MftValidLength - virtualOffset, data.BytesPerSector));
                if (lcn >= 0)
                {
                    chunks.Add(((lcn * data.BytesPerCluster) + done, length, virtualOffset / data.RecordSize));
                }

                done += length;
                virtualOffset += length;
            }
        }

        var free = new BlockingCollection<nint>();
        var full = new BlockingCollection<(nint Buffer, int Chunk, int Error)>();
        var buffers = new nint[BufferCount];
        for (var i = 0; i < BufferCount; i++)
        {
            buffers[i] = (nint)NativeMemory.AlignedAlloc(ChunkSize, 4096);
            free.Add(buffers[i]);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = Task.Run(() =>
        {
            try
            {
                for (var c = 0; c < chunks.Count; c++)
                {
                    var buffer = free.Take(stop.Token);
                    var ok = ReadAt(volume, chunks[c].DiskOffset, (byte*)buffer, chunks[c].Length);
                    full.Add((buffer, c, ok ? 0 : Marshal.GetLastPInvokeError()), stop.Token);
                    if (!ok)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Parser stopped or the scan was cancelled.
            }
            finally
            {
                full.CompleteAdding();
            }
        });

        try
        {
            long inUse = 0;
            foreach (var (buffer, c, error) in full.GetConsumingEnumerable(stop.Token))
            {
                if (error != 0)
                {
                    return $"cannot read the file table ({Win32Errors.Describe(error)})";
                }

                var (_, length, firstRecord) = chunks[c];
                var records = length / data.RecordSize;
                for (var i = 0; i < records; i++)
                {
                    var number = firstRecord + i;
                    if (number >= _recordCount)
                    {
                        break;
                    }

                    if (ParseRecord(new Span<byte>((byte*)buffer + (i * data.RecordSize), data.RecordSize), (int)number))
                    {
                        inUse++;
                    }
                }

                free.Add(buffer);
                RecordsInUse = inUse;
                progress?.Invoke(inUse);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            stop.Cancel();
            reader.Wait(CancellationToken.None);
            foreach (var buffer in buffers)
            {
                NativeMemory.AlignedFree((void*)buffer);
            }

            free.Dispose();
            full.Dispose();
        }
    }

    /// <summary>Takes what is needed from one file record; true if it is an in-use base record.</summary>
    private bool ParseRecord(Span<byte> record, int number)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature || !ApplyFixups(record))
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        if ((flags & 1) == 0)
        {
            return false;
        }

        // An extension record holds attributes that did not fit in its base record.
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        int owner;
        ushort ownerSequence;
        if (baseReference == 0)
        {
            owner = number;
            ownerSequence = sequence;
            _sequence[number] = sequence;
            _flags[number] = (byte)(FlagInUse | ((flags & 2) != 0 ? FlagDirectory : 0));
        }
        else
        {
            var baseRecord = (long)(baseReference & 0xFFFFFFFFFFFF);
            if (baseRecord >= _recordCount)
            {
                return false;
            }

            owner = (int)baseRecord;
            ownerSequence = (ushort)(baseReference >> 48);
        }

        foreach (var item in Attributes(record))
        {
            var attribute = item.Data;
            switch (item.Type)
            {
                case AttributeStandardInformation when attribute[8] == 0:
                    var info = Resident(attribute);
                    if (info.Length >= 36)
                    {
                        _lastWrite[owner] = BinaryPrimitives.ReadInt64LittleEndian(info[8..]);
                        _attributes[owner] = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
                    }

                    break;

                case AttributeFileName when attribute[8] == 0:
                    AddName(Resident(attribute), owner, ownerSequence);
                    break;

                case AttributeData when attribute[9] == 0: // the unnamed (main) data stream
                    if (attribute[8] == 0)
                    {
                        _size[owner] = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                    }
                    else if (BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]) == 0)
                    {
                        // Only the first piece (starting at cluster 0) carries the stream size.
                        _size[owner] = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                    }

                    break;
            }
        }

        return baseReference == 0;
    }

    private void AddName(ReadOnlySpan<byte> value, int owner, ushort ownerSequence)
    {
        if (value.Length < 66)
        {
            return;
        }

        var nameLength = value[64];
        var nameSpace = value[65];
        if (nameSpace == NamespaceDos || nameLength == 0 || value.Length < 66 + (nameLength * 2))
        {
            return; // 8.3 short names are not listed separately
        }

        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var parent = (long)(parentReference & 0xFFFFFFFFFFFF);
        if (parent >= _recordCount)
        {
            return;
        }

        if (_linkCount == _linkRecord.Length)
        {
            var capacity = _linkRecord.Length + (_linkRecord.Length / 2);
            Array.Resize(ref _linkRecord, capacity);
            Array.Resize(ref _linkSequence, capacity);
            Array.Resize(ref _linkParent, capacity);
            Array.Resize(ref _linkParentSequence, capacity);
            Array.Resize(ref _linkNameStart, capacity);
            Array.Resize(ref _linkNameLength, capacity);
        }

        if (_charCount + nameLength > _chars.Length)
        {
            Array.Resize(ref _chars, (int)Math.Min(Array.MaxLength, Math.Max(_chars.Length + (_chars.Length / 2L), _charCount + nameLength)));
        }

        MemoryMarshal.Cast<byte, char>(value.Slice(66, nameLength * 2)).CopyTo(_chars.AsSpan(_charCount));
        var link = _linkCount++;
        _linkRecord[link] = owner;
        _linkSequence[link] = ownerSequence;
        _linkParent[link] = (int)parent;
        _linkParentSequence[link] = (ushort)(parentReference >> 48);
        _linkNameStart[link] = _charCount;
        _linkNameLength[link] = nameLength;
        _charCount += nameLength;
    }

    /// <summary>Groups names by parent folder. Names of deleted or reused records and metadata files are dropped.</summary>
    private void BuildChildren()
    {
        var valid = new bool[_linkCount];
        var counts = new int[_recordCount + 1];
        for (var link = 0; link < _linkCount; link++)
        {
            var record = _linkRecord[link];
            var parent = _linkParent[link];
            if (record < FirstUserRecord ||
                (_flags[record] & FlagInUse) == 0 || _sequence[record] != _linkSequence[link] ||
                (_flags[parent] & FlagDirectory) == 0 || _sequence[parent] != _linkParentSequence[link])
            {
                continue;
            }

            valid[link] = true;
            counts[parent + 1]++;
        }

        for (var i = 1; i < counts.Length; i++)
        {
            counts[i] += counts[i - 1];
        }

        _childStart = counts;
        _childLinks = new int[counts[^1]];
        var fill = (int[])counts.Clone();
        for (var link = 0; link < _linkCount; link++)
        {
            if (valid[link])
            {
                _childLinks[fill[_linkParent[link]]++] = link;
            }
        }
    }

    /// <summary>Undoes the "update sequence" protection: the last two bytes of each 512-byte stride.</summary>
    private static bool ApplyFixups(Span<byte> record)
    {
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (count < 2 || offset + (count * 2) > record.Length)
        {
            return false;
        }

        var stride = record.Length / (count - 1);
        var check = BinaryPrimitives.ReadUInt16LittleEndian(record[offset..]);
        for (var i = 1; i < count; i++)
        {
            var end = (i * stride) - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check)
            {
                return false; // torn or damaged record
            }

            record[end] = record[offset + (i * 2)];
            record[end + 1] = record[offset + (i * 2) + 1];
        }

        return true;
    }

    private static AttributeEnumerator Attributes(ReadOnlySpan<byte> record) => new(record);

    private static ReadOnlySpan<byte> Resident(ReadOnlySpan<byte> attribute)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(attribute[16..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        return offset + length <= attribute.Length && length >= 0 ? attribute.Slice(offset, length) : [];
    }

    private static unsafe bool ReadAt(SafeFileHandle volume, long offset, byte* buffer, int length)
    {
        var overlapped = new NativeOverlapped
        {
            OffsetLow = (int)(offset & 0xFFFFFFFF),
            OffsetHigh = (int)(offset >> 32),
        };
        return NativeMethods.ReadFile(volume, buffer, length, out var read, &overlapped) && read == length;
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private readonly record struct VolumeData(int BytesPerSector, int BytesPerCluster, int RecordSize, long MftValidLength, long MftStartLcn);

    /// <summary>One attribute of a file record: its type and all of its bytes (header included).</summary>
    private readonly ref struct RecordAttribute(uint type, ReadOnlySpan<byte> data)
    {
        public uint Type { get; } = type;

        public ReadOnlySpan<byte> Data { get; } = data;
    }

    /// <summary>Walks the attributes of a file record.</summary>
    private ref struct AttributeEnumerator(ReadOnlySpan<byte> record)
    {
        private readonly ReadOnlySpan<byte> _record = record;
        private int _offset = -1;
        private int _end;

        public RecordAttribute Current { get; private set; }

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_offset < 0)
            {
                _offset = BinaryPrimitives.ReadUInt16LittleEndian(_record[20..]);
                _end = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(_record[24..]), _record.Length);
            }

            if (_offset + 8 > _end)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(_record[_offset..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(_record[(_offset + 4)..]);
            if (type == AttributeEnd || length < 24 || _offset + length > _end)
            {
                return false;
            }

            Current = new RecordAttribute(type, _record.Slice(_offset, length));
            _offset += length;
            return true;
        }
    }
}
