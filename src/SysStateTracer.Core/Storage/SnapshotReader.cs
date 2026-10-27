using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Storage;

/// <summary>What the main window needs to list a snapshot, read without loading its data.</summary>
public sealed record SnapshotInfo(string Path, SnapshotMeta Meta, long FileLength, int FormatMajor, int FormatMinor)
{
    /// <summary>Current snapshot name: the file name without .stsnap (plan 2.10).</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// Reads .stsnap files. <see cref="ReadInfo"/> reads only the header and metadata (fast, for listing);
/// <see cref="Load(string, SnapshotParts, CancellationToken)"/> decompresses the requested parts in parallel.
/// Every block is checked against its hash; any damage raises <see cref="SnapshotFormatException"/>.
/// </summary>
public sealed class SnapshotReader : IDisposable
{
    private const int MaxMetaLength = 16 * 1024 * 1024;

    private readonly SafeFileHandle _file;
    private readonly Dictionary<SectionKind, SnapshotFormat.SectionEntry> _sections;

    private SnapshotReader(string path, SafeFileHandle file, SnapshotInfo info, Dictionary<SectionKind, SnapshotFormat.SectionEntry> sections)
    {
        Path = path;
        _file = file;
        Info = info;
        _sections = sections;
    }

    public string Path { get; }

    public SnapshotInfo Info { get; }

    /// <summary>Reads only header and metadata.</summary>
    public static SnapshotInfo ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var file = OpenFile(path);
        return ReadHeaderAndMeta(path, file).Info;
    }

    /// <summary>Opens a snapshot for reading sections; dispose to close the file.</summary>
    public static SnapshotReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = OpenFile(path);
        try
        {
            var (info, header) = ReadHeaderAndMeta(path, file);
            var sections = ReadSectionTable(path, file, header);
            return new SnapshotReader(path, file, info, sections);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Opens, loads the requested parts, and closes the file.</summary>
    public static Snapshot Load(string path, SnapshotParts parts = SnapshotParts.All, CancellationToken cancellationToken = default)
    {
        using var reader = Open(path);
        return reader.Load(parts, cancellationToken);
    }

    public Snapshot Load(SnapshotParts parts = SnapshotParts.All, CancellationToken cancellationToken = default)
    {
        var names = new NameTable(
            ReadColumn<char>(SectionKind.NameChars, cancellationToken),
            ReadColumn<int>(SectionKind.NameOffsets, cancellationToken, emptyValue: [0]));
        ValidateNameTable(names);

        var fs = parts.HasFlag(SnapshotParts.FileSystem) ? LoadFileSystem(names, cancellationToken) : null;
        var reg = parts.HasFlag(SnapshotParts.Registry) ? LoadRegistry(names, parts.HasFlag(SnapshotParts.RegistryData), cancellationToken) : null;
        var services = parts.HasFlag(SnapshotParts.Services) ? LoadServices(names, cancellationToken) : null;
        var skipped = parts.HasFlag(SnapshotParts.Skipped) ? LoadSkipped(cancellationToken) : null;

        var loaded = parts;
        if (!parts.HasFlag(SnapshotParts.Registry))
        {
            loaded &= ~SnapshotParts.RegistryData;
        }

        return new Snapshot(Info.Meta, names, fs, reg, services, skipped, loaded);
    }

    public void Dispose() => _file.Dispose();

    private FsTree LoadFileSystem(NameTable names, CancellationToken ct)
    {
        var parent = FromParentDeltas(ReadColumn<int>(SectionKind.FsParentDelta, ct));
        var name = ReadColumn<int>(SectionKind.FsName, ct);
        ValidateIds(name, names.Count, "file-system names");
        try
        {
            return new FsTree(
                parent,
                name,
                ReadColumn<uint>(SectionKind.FsAttributes, ct),
                ReadColumn<long>(SectionKind.FsSize, ct),
                ReadColumn<long>(SectionKind.FsLastWrite, ct),
                ReadColumn<int>(SectionKind.FsDescendants, ct),
                ReadColumn<int>(SectionKind.FsChildFolders, ct),
                ReadColumn<int>(SectionKind.FsChildFiles, ct),
                ReadColumn<ulong>(SectionKind.FsDigest, ct),
                ReadColumn<long>(SectionKind.FsFileId, ct));
        }
        catch (ArgumentException ex)
        {
            throw Corrupt("The file-system data is inconsistent.", ex);
        }
    }

    private RegTree LoadRegistry(NameTable names, bool withData, CancellationToken ct)
    {
        var parent = FromParentDeltas(ReadColumn<int>(SectionKind.RegParentDelta, ct));
        var name = ReadColumn<int>(SectionKind.RegName, ct);
        var valueName = ReadColumn<int>(SectionKind.ValName, ct);
        ValidateIds(name, names.Count, "registry key names");
        ValidateIds(valueName, names.Count, "registry value names");

        var blobs = BlobStore.Empty;
        if (withData)
        {
            try
            {
                blobs = new BlobStore(
                    ReadColumn<byte>(SectionKind.BlobData, ct),
                    ReadColumn<long>(SectionKind.BlobOffsets, ct, emptyValue: [0]));
            }
            catch (ArgumentException ex)
            {
                throw Corrupt("The registry value data is inconsistent.", ex);
            }
        }

        try
        {
            return new RegTree(
                parent,
                name,
                ReadColumn<long>(SectionKind.RegLastWrite, ct),
                ReadColumn<int>(SectionKind.RegFirstValue, ct),
                ReadColumn<int>(SectionKind.RegValueCount, ct),
                ReadColumn<int>(SectionKind.RegDescendants, ct),
                ReadColumn<ulong>(SectionKind.RegDigest, ct),
                valueName,
                ReadColumn<uint>(SectionKind.ValType, ct),
                ReadColumn<ulong>(SectionKind.ValDataHash, ct),
                ReadColumn<int>(SectionKind.ValBlob, ct),
                ReadColumn<int>(SectionKind.ValDataLength, ct),
                blobs);
        }
        catch (ArgumentException ex)
        {
            throw Corrupt("The registry data is inconsistent.", ex);
        }
    }

    private ServiceTable LoadServices(NameTable names, CancellationToken ct)
    {
        var name = ReadColumn<int>(SectionKind.SvcName, ct);
        var displayName = ReadColumn<int>(SectionKind.SvcDisplayName, ct);
        ValidateIds(name, names.Count, "service names");
        ValidateIds(displayName, names.Count, "service display names");
        try
        {
            return new ServiceTable(
                name,
                displayName,
                ReadColumn<uint>(SectionKind.SvcType, ct),
                ReadColumn<uint>(SectionKind.SvcState, ct),
                ReadColumn<uint>(SectionKind.SvcStartType, ct),
                ReadColumn<ServiceFlags>(SectionKind.SvcFlags, ct));
        }
        catch (ArgumentException ex)
        {
            throw Corrupt("The services data is inconsistent.", ex);
        }
    }

    private List<SkippedItem> LoadSkipped(CancellationToken ct)
    {
        var chars = ReadColumn<char>(SectionKind.SkippedChars, ct);
        var offsets = ReadColumn<int>(SectionKind.SkippedOffsets, ct, emptyValue: [0]);
        var errors = ReadColumn<int>(SectionKind.SkippedErrors, ct);
        if (offsets.Length != errors.Length + 1)
        {
            throw Corrupt("The skipped-items list is inconsistent.");
        }

        var items = new List<SkippedItem>(errors.Length);
        for (var i = 0; i < errors.Length; i++)
        {
            var start = offsets[i];
            var end = offsets[i + 1];
            if (start < 0 || end < start || end > chars.Length)
            {
                throw Corrupt("The skipped-items list is inconsistent.");
            }

            items.Add(new SkippedItem(new string(chars, start, end - start), errors[i]));
        }

        return items;
    }

    /// <summary>Decompresses one column. A missing section gives <paramref name="emptyValue"/> (or an empty array).</summary>
    internal T[] ReadColumn<T>(SectionKind kind, CancellationToken ct, T[]? emptyValue = null)
        where T : unmanaged
    {
        ct.ThrowIfCancellationRequested();
        if (!_sections.TryGetValue(kind, out var section))
        {
            return emptyValue ?? [];
        }

        var elementSize = Unsafe.SizeOf<T>();
        if (section.ElementSize != elementSize || section.RawLength % elementSize != 0 || section.RawLength > Array.MaxLength)
        {
            throw Corrupt($"Section {kind} has an unexpected size.");
        }

        var result = new T[section.RawLength / elementSize];
        var rawStart = new int[section.Blocks.Length];
        long position = 0;
        for (var b = 0; b < section.Blocks.Length; b++)
        {
            rawStart[b] = (int)position;
            position += section.Blocks[b].RawLength;
        }

        if (position != section.RawLength)
        {
            throw Corrupt($"Section {kind} blocks do not add up.");
        }

        var parallel = new ParallelOptions { CancellationToken = ct };
        RunParallel(section.Blocks.Length, parallel, b =>
        {
            var block = section.Blocks[b];
            var destination = MemoryMarshal.AsBytes(result.AsSpan()).Slice(rawStart[b], block.RawLength);
            if (block.Codec == SnapshotFormat.CodecStored)
            {
                if (block.StoredLength != block.RawLength)
                {
                    throw Corrupt($"Section {kind} has a damaged block.");
                }

                ReadExactly(block.Offset, destination);
                VerifyHash(destination, block.Hash, kind);
                return;
            }

            if (block.Codec != SnapshotFormat.CodecBrotli)
            {
                throw Corrupt($"Section {kind} uses an unknown compression.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(block.StoredLength);
            try
            {
                var stored = buffer.AsSpan(0, block.StoredLength);
                ReadExactly(block.Offset, stored);
                VerifyHash(stored, block.Hash, kind);
                if (!BrotliDecoder.TryDecompress(stored, destination, out var written) || written != block.RawLength)
                {
                    throw Corrupt($"Section {kind} could not be decompressed.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        return result;
    }

    private static SafeFileHandle OpenFile(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);

    private static (SnapshotInfo Info, SnapshotFormat.HeaderFields Header) ReadHeaderAndMeta(string path, SafeFileHandle file)
    {
        var actualLength = RandomAccess.GetLength(file);
        if (actualLength < SnapshotFormat.HeaderSize)
        {
            throw new SnapshotFormatException(path, "The file is too short to be a snapshot.");
        }

        var headerBytes = new byte[SnapshotFormat.HeaderSize];
        ReadExactly(file, path, 0, headerBytes);
        var header = SnapshotFormat.ReadHeader(headerBytes, path);

        if (header.FileLength != actualLength)
        {
            throw new SnapshotFormatException(path, "The snapshot file is incomplete or was cut off while copying.");
        }

        if (header.MetaLength <= 0 || header.MetaLength > MaxMetaLength ||
            header.MetaOffset < SnapshotFormat.HeaderSize || header.MetaOffset + header.MetaLength > actualLength)
        {
            throw new SnapshotFormatException(path, "The snapshot metadata is damaged.");
        }

        var metaBytes = new byte[header.MetaLength];
        ReadExactly(file, path, header.MetaOffset, metaBytes);
        if (SnapshotFormat.Hash(metaBytes) != header.MetaHash)
        {
            throw new SnapshotFormatException(path, "The snapshot metadata is damaged.");
        }

        SnapshotMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize(metaBytes, SnapshotMetaJsonContext.Default.SnapshotMeta);
        }
        catch (JsonException ex)
        {
            throw new SnapshotFormatException(path, "The snapshot metadata is damaged.", ex);
        }

        if (meta is null)
        {
            throw new SnapshotFormatException(path, "The snapshot metadata is missing.");
        }

        return (new SnapshotInfo(path, meta, actualLength, SnapshotFormat.MajorVersion, header.Minor), header);
    }

    private static Dictionary<SectionKind, SnapshotFormat.SectionEntry> ReadSectionTable(
        string path, SafeFileHandle file, SnapshotFormat.HeaderFields header)
    {
        if (header.TableLength < 0 || header.TableOffset < SnapshotFormat.HeaderSize ||
            header.TableOffset + header.TableLength > header.FileLength || header.SectionCount < 0)
        {
            throw new SnapshotFormatException(path, "The snapshot section table is damaged.");
        }

        var table = new byte[header.TableLength];
        ReadExactly(file, path, header.TableOffset, table);
        if (SnapshotFormat.Hash(table) != header.TableHash)
        {
            throw new SnapshotFormatException(path, "The snapshot section table is damaged.");
        }

        var sections = new Dictionary<SectionKind, SnapshotFormat.SectionEntry>(header.SectionCount);
        ReadOnlySpan<byte> span = table;
        for (var s = 0; s < header.SectionCount; s++)
        {
            if (span.Length < SnapshotFormat.SectionEntrySize)
            {
                throw new SnapshotFormatException(path, "The snapshot section table is damaged.");
            }

            var kind = (SectionKind)BinaryPrimitives.ReadUInt16LittleEndian(span);
            var elementSize = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            var blockCount = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
            var rawLength = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
            span = span[SnapshotFormat.SectionEntrySize..];

            if (blockCount < 0 || rawLength < 0 || elementSize == 0 ||
                span.Length < (long)blockCount * SnapshotFormat.BlockEntrySize)
            {
                throw new SnapshotFormatException(path, "The snapshot section table is damaged.");
            }

            var blocks = new SnapshotFormat.BlockEntry[blockCount];
            for (var b = 0; b < blockCount; b++)
            {
                var block = new SnapshotFormat.BlockEntry(
                    Offset: BinaryPrimitives.ReadInt64LittleEndian(span),
                    StoredLength: BinaryPrimitives.ReadInt32LittleEndian(span[8..]),
                    RawLength: BinaryPrimitives.ReadInt32LittleEndian(span[12..]),
                    Codec: span[16],
                    Hash: BinaryPrimitives.ReadUInt64LittleEndian(span[24..]));
                span = span[SnapshotFormat.BlockEntrySize..];

                if (block.Offset < SnapshotFormat.HeaderSize || block.StoredLength < 0 ||
                    block.RawLength < 0 || block.RawLength > SnapshotFormat.BlockSize ||
                    block.StoredLength > SnapshotFormat.BlockSize * 2 ||
                    block.Offset + block.StoredLength > header.TableOffset)
                {
                    throw new SnapshotFormatException(path, "The snapshot section table is damaged.");
                }

                blocks[b] = block;
            }

            // Unknown sections (from a newer minor version) are ignored.
            sections[kind] = new SnapshotFormat.SectionEntry(kind, elementSize, rawLength, blocks);
        }

        return sections;
    }

    private void ReadExactly(long offset, Span<byte> destination) => ReadExactly(_file, Path, offset, destination);

    private static void ReadExactly(SafeFileHandle file, string path, long offset, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(file, destination, offset);
            if (read <= 0)
            {
                throw new SnapshotFormatException(path, "The snapshot file ended unexpectedly.");
            }

            destination = destination[read..];
            offset += read;
        }
    }

    private void VerifyHash(ReadOnlySpan<byte> data, ulong expected, SectionKind kind)
    {
        if (SnapshotFormat.Hash(data) != expected)
        {
            throw Corrupt($"Section {kind} is damaged (checksum mismatch).");
        }
    }

    private int[] FromParentDeltas(int[] delta)
    {
        for (var i = 0; i < delta.Length; i++)
        {
            var d = delta[i];
            if (d < 0 || d > i)
            {
                throw Corrupt("The tree structure is damaged.");
            }

            delta[i] = d == 0 ? -1 : i - d;
        }

        return delta;
    }

    private void ValidateIds(int[] ids, int count, string what)
    {
        foreach (var id in ids)
        {
            if ((uint)id >= (uint)count)
            {
                throw Corrupt($"The {what} refer to missing names.");
            }
        }
    }

    private void ValidateNameTable(NameTable names)
    {
        var offsets = names.Offsets;
        for (var i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw Corrupt("The name table is damaged.");
            }
        }
    }

    private SnapshotFormatException Corrupt(string reason, Exception? inner = null) => new(Path, reason, inner);

    /// <summary>Parallel.For that rethrows the first real error instead of an AggregateException.</summary>
    private static void RunParallel(int count, ParallelOptions options, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, options, body);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
