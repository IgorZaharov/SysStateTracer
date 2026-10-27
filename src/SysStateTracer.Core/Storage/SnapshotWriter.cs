using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Storage;

public sealed class SnapshotWriteOptions
{
    /// <summary>Brotli quality 0–11. Low levels are several times faster for a small size cost.</summary>
    public int CompressionQuality { get; init; } = 3;

    /// <summary>Replace an existing file with the same name.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Threads used for compression; -1 = all cores.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>Also write file reference numbers and this cache description (SysStateTracer.cache only).</summary>
    internal byte[]? CacheInfo { get; init; }
}

/// <summary>
/// Writes a <see cref="Snapshot"/> to a .stsnap file (format: docs/format.md).
/// The file appears only when complete: data goes to "name.stsnap.tmp", which is renamed at the end
/// and deleted on failure or cancellation.
/// </summary>
public static class SnapshotWriter
{
    private const int BrotliWindow = 22;

    /// <summary>
    /// Writes <paramref name="snapshot"/> to <paramref name="path"/>.
    /// Fills in <see cref="SnapshotMeta.Counts"/> and <see cref="SnapshotTimings.WriteMs"/> of the snapshot's meta.
    /// </summary>
    public static void Write(string path, Snapshot snapshot, SnapshotWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new SnapshotWriteOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CompressionQuality, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CompressionQuality, 11);

        if (!options.Overwrite && File.Exists(path))
        {
            throw new IOException($"A snapshot named '{Path.GetFileName(path)}' already exists.");
        }

        var watch = Stopwatch.StartNew();
        var meta = snapshot.Meta;
        FillCounts(snapshot);

        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(new byte[SnapshotFormat.HeaderSize]);

                var writer = new SectionWriter(stream, options, cancellationToken);
                WriteNames(writer, snapshot.Names);
                WriteFileSystem(writer, snapshot.FileSystem, options.CacheInfo is not null);
                WriteRegistry(writer, snapshot.Registry);
                WriteServices(writer, snapshot.Services);
                WriteSkipped(writer, snapshot.Skipped);
                if (options.CacheInfo is not null)
                {
                    writer.Write(SectionKind.CacheInfo, options.CacheInfo);
                }

                var table = writer.BuildTable();
                var tableOffset = stream.Position;
                stream.Write(table);

                meta.Timings.WriteMs = watch.ElapsedMilliseconds;
                var metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, SnapshotMetaJsonContext.Default.SnapshotMeta);
                var metaOffset = stream.Position;
                stream.Write(metaBytes);

                Span<byte> header = stackalloc byte[SnapshotFormat.HeaderSize];
                SnapshotFormat.WriteHeader(header, new SnapshotFormat.HeaderFields(
                    FileLength: stream.Position,
                    TableOffset: tableOffset,
                    TableLength: table.Length,
                    SectionCount: writer.SectionCount,
                    TableHash: SnapshotFormat.Hash(table),
                    MetaOffset: metaOffset,
                    MetaLength: metaBytes.Length,
                    CreatedUtcTicks: meta.StartedAt.UtcTicks,
                    MetaHash: SnapshotFormat.Hash(metaBytes)));

                stream.Position = 0;
                stream.Write(header);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, options.Overwrite);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void FillCounts(Snapshot snapshot)
    {
        var fs = snapshot.FileSystem;
        long folders = 0;
        for (var i = 0; i < fs.Count; i++)
        {
            if (fs.IsDirectory(i))
            {
                folders++;
            }
        }

        var counts = snapshot.Meta.Counts;
        counts.Folders = folders;
        counts.Files = fs.Count - folders;
        counts.RegistryKeys = snapshot.Registry.KeyCount;
        counts.RegistryValues = snapshot.Registry.ValueCount;
        counts.Services = snapshot.Services.Count;
        counts.Skipped = snapshot.Skipped.Count;
    }

    private static void WriteNames(SectionWriter w, NameTable names)
    {
        w.Write(SectionKind.NameChars, names.Chars);
        w.Write(SectionKind.NameOffsets, names.Offsets);
    }

    private static void WriteFileSystem(SectionWriter w, FsTree fs, bool fileIds)
    {
        w.Write(SectionKind.FsParentDelta, ToParentDeltas(fs.Parent));
        w.Write(SectionKind.FsName, fs.Name);
        w.Write(SectionKind.FsAttributes, fs.Attributes);
        w.Write(SectionKind.FsSize, fs.Size);
        w.Write(SectionKind.FsLastWrite, fs.LastWriteTime);
        w.Write(SectionKind.FsDescendants, fs.Descendants);
        w.Write(SectionKind.FsChildFolders, fs.ChildFolders);
        w.Write(SectionKind.FsChildFiles, fs.ChildFiles);
        w.Write(SectionKind.FsDigest, fs.Digest);
        if (fileIds && fs.HasFileIds)
        {
            w.Write(SectionKind.FsFileId, fs.FileId);
        }
    }

    private static void WriteRegistry(SectionWriter w, RegTree reg)
    {
        w.Write(SectionKind.RegParentDelta, ToParentDeltas(reg.Parent));
        w.Write(SectionKind.RegName, reg.Name);
        w.Write(SectionKind.RegLastWrite, reg.LastWriteTime);
        w.Write(SectionKind.RegFirstValue, reg.FirstValue);
        w.Write(SectionKind.RegValueCount, reg.KeyValueCount);
        w.Write(SectionKind.RegDescendants, reg.Descendants);
        w.Write(SectionKind.RegDigest, reg.Digest);

        w.Write(SectionKind.ValName, reg.ValueName);
        w.Write(SectionKind.ValType, reg.ValueDataType);
        w.Write(SectionKind.ValDataHash, reg.ValueDataHash);
        w.Write(SectionKind.ValBlob, reg.ValueBlob);
        w.Write(SectionKind.ValDataLength, reg.ValueDataLength);

        w.Write(SectionKind.BlobOffsets, reg.Blobs.Offsets);
        w.Write(SectionKind.BlobData, reg.Blobs.Data);
    }

    private static void WriteServices(SectionWriter w, ServiceTable services)
    {
        w.Write(SectionKind.SvcName, services.Name);
        w.Write(SectionKind.SvcDisplayName, services.DisplayName);
        w.Write(SectionKind.SvcType, services.ServiceType);
        w.Write(SectionKind.SvcState, services.State);
        w.Write(SectionKind.SvcStartType, services.StartType);
        w.Write(SectionKind.SvcFlags, services.Flags);
    }

    private static void WriteSkipped(SectionWriter w, IReadOnlyList<SkippedItem> skipped)
    {
        var offsets = new int[skipped.Count + 1];
        var total = 0;
        for (var i = 0; i < skipped.Count; i++)
        {
            total += skipped[i].Path.Length;
            offsets[i + 1] = total;
        }

        var chars = new char[total];
        var errors = new int[skipped.Count];
        for (var i = 0; i < skipped.Count; i++)
        {
            skipped[i].Path.CopyTo(chars.AsSpan(offsets[i]));
            errors[i] = skipped[i].Error;
        }

        w.Write(SectionKind.SkippedChars, chars);
        w.Write(SectionKind.SkippedOffsets, offsets);
        w.Write(SectionKind.SkippedErrors, errors);
    }

    /// <summary>Parent links stored as "distance back to the parent" (0 = root): small numbers compress well.</summary>
    private static int[] ToParentDeltas(ReadOnlySpan<int> parent)
    {
        var delta = new int[parent.Length];
        for (var i = 0; i < parent.Length; i++)
        {
            delta[i] = parent[i] < 0 ? 0 : i - parent[i];
        }

        return delta;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover .tmp files are harmless and are overwritten next time.
        }
    }

    /// <summary>Compresses column arrays block by block in parallel and appends them to the stream.</summary>
    private sealed class SectionWriter(FileStream stream, SnapshotWriteOptions options, CancellationToken cancellationToken)
    {
        private readonly List<SnapshotFormat.SectionEntry> _sections = [];

        public int SectionCount => _sections.Count;

        /// <summary>
        /// Compresses <paramref name="column"/> without copying it. Blocks are compressed a batch at a time
        /// and written right away, so only one batch of compressed blocks is held in memory.
        /// </summary>
        public unsafe void Write<T>(SectionKind kind, ReadOnlySpan<T> column)
            where T : unmanaged
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elementSize = Unsafe.SizeOf<T>();
            var rawLength = (long)column.Length * elementSize;
            var blockCount = (int)((rawLength + SnapshotFormat.BlockSize - 1) / SnapshotFormat.BlockSize);
            var blocks = new SnapshotFormat.BlockEntry[blockCount];
            var threads = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
            var batchSize = Math.Max(1, threads * 2);
            var buffers = new byte[batchSize][];
            var storedLength = new int[batchSize];
            var codec = new byte[batchSize];
            var hash = new ulong[batchSize];
            var quality = options.CompressionQuality;

            var parallel = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };

            fixed (T* pinned = column)
            {
                // The column stays pinned while the batch runs; lambdas cannot capture the pointer itself.
                var address = (nint)pinned;
                for (var first = 0; first < blockCount; first += batchSize)
                {
                    var count = Math.Min(batchSize, blockCount - first);
                    var batchStart = first;
                    Parallel.For(0, count, parallel, i =>
                    {
                        var start = (long)(batchStart + i) * SnapshotFormat.BlockSize;
                        var length = (int)Math.Min(SnapshotFormat.BlockSize, rawLength - start);
                        var source = new ReadOnlySpan<byte>((byte*)address + start, length);

                        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, BrotliEncoder.GetMaxCompressedLength(length)));
                        if (BrotliEncoder.TryCompress(source, buffer, out var written, quality, BrotliWindow) && written < length)
                        {
                            codec[i] = SnapshotFormat.CodecBrotli;
                            storedLength[i] = written;
                        }
                        else
                        {
                            source.CopyTo(buffer);
                            codec[i] = SnapshotFormat.CodecStored;
                            storedLength[i] = length;
                        }

                        buffers[i] = buffer;
                        hash[i] = SnapshotFormat.Hash(buffer.AsSpan(0, storedLength[i]));
                    });

                    for (var i = 0; i < count; i++)
                    {
                        var b = first + i;
                        var rawBlock = (int)Math.Min(SnapshotFormat.BlockSize, rawLength - ((long)b * SnapshotFormat.BlockSize));
                        blocks[b] = new SnapshotFormat.BlockEntry(stream.Position, storedLength[i], rawBlock, codec[i], hash[i]);
                        stream.Write(buffers[i], 0, storedLength[i]);
                        ArrayPool<byte>.Shared.Return(buffers[i]);
                        buffers[i] = null!;
                    }
                }
            }

            _sections.Add(new SnapshotFormat.SectionEntry(kind, elementSize, rawLength, blocks));
        }

        public byte[] BuildTable()
        {
            var size = 0;
            foreach (var s in _sections)
            {
                size += SnapshotFormat.SectionEntrySize + (s.Blocks.Length * SnapshotFormat.BlockEntrySize);
            }

            var table = new byte[size];
            var span = table.AsSpan();
            foreach (var s in _sections)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)s.Kind);
                BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)s.ElementSize);
                BinaryPrimitives.WriteInt32LittleEndian(span[4..], s.Blocks.Length);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], s.RawLength);
                span = span[SnapshotFormat.SectionEntrySize..];

                foreach (var block in s.Blocks)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(span, block.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(span[8..], block.StoredLength);
                    BinaryPrimitives.WriteInt32LittleEndian(span[12..], block.RawLength);
                    span[16] = block.Codec;
                    BinaryPrimitives.WriteUInt64LittleEndian(span[24..], block.Hash);
                    span = span[SnapshotFormat.BlockEntrySize..];
                }
            }

            return table;
        }
    }
}
