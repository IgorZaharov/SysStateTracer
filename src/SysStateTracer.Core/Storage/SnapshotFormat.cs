using System.Buffers.Binary;
using System.IO.Hashing;

namespace SysStateTracer.Core.Storage;

/// <summary>Constants and binary layout of the .stsnap format, version 1 (see docs/format.md).</summary>
internal static class SnapshotFormat
{
    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 0;
    public const int HeaderSize = 512;
    public const int BlockSize = 1 << 20;

    /// <summary>"STSNAP" + LF + Ctrl-Z: detects text-mode transfers and truncated copies.</summary>
    public static ReadOnlySpan<byte> Magic => "STSNAP\n\x1A"u8;

    public const int SectionEntrySize = 16;
    public const int BlockEntrySize = 32;

    public const byte CodecStored = 0;
    public const byte CodecBrotli = 1;

    // Header field offsets
    public const int OffMagic = 0;
    public const int OffMajor = 8;
    public const int OffMinor = 10;
    public const int OffHeaderSize = 12;
    public const int OffFileLength = 16;
    public const int OffTableOffset = 24;
    public const int OffTableLength = 32;
    public const int OffSectionCount = 36;
    public const int OffTableHash = 40;
    public const int OffMetaOffset = 48;
    public const int OffMetaLength = 56;
    public const int OffBlockSize = 60;
    public const int OffCreatedTicks = 64;
    public const int OffMetaHash = 72;
    public const int OffHeaderHash = HeaderSize - 8;

    public static ulong Hash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);

    public static void WriteHeader(Span<byte> header, in HeaderFields fields)
    {
        header[..HeaderSize].Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[OffMajor..], MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[OffMinor..], MinorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[OffHeaderSize..], HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[OffFileLength..], fields.FileLength);
        BinaryPrimitives.WriteInt64LittleEndian(header[OffTableOffset..], fields.TableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header[OffTableLength..], fields.TableLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[OffSectionCount..], fields.SectionCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[OffTableHash..], fields.TableHash);
        BinaryPrimitives.WriteInt64LittleEndian(header[OffMetaOffset..], fields.MetaOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header[OffMetaLength..], fields.MetaLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[OffBlockSize..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[OffCreatedTicks..], fields.CreatedUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(header[OffMetaHash..], fields.MetaHash);
        BinaryPrimitives.WriteUInt64LittleEndian(header[OffHeaderHash..], Hash(header[..OffHeaderHash]));
    }

    /// <summary>Parses and checks a header; throws <see cref="SnapshotFormatException"/> when it is not usable.</summary>
    public static HeaderFields ReadHeader(ReadOnlySpan<byte> header, string path)
    {
        if (header.Length < HeaderSize || !header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new SnapshotFormatException(path, "This is not a SysStateTracer snapshot file.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(header[OffMajor..]);
        if (major != MajorVersion)
        {
            throw new SnapshotFormatException(
                path,
                major > MajorVersion
                    ? $"The snapshot was made by a newer SysStateTracer (format {major}). Please update SysStateTracer."
                    : $"Unsupported snapshot format version {major}.");
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(header[OffHeaderHash..]) != Hash(header[..OffHeaderHash]))
        {
            throw new SnapshotFormatException(path, "The snapshot header is damaged.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(header[OffHeaderSize..]) != HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(header[OffBlockSize..]) != BlockSize)
        {
            throw new SnapshotFormatException(path, "The snapshot header has unexpected sizes.");
        }

        return new HeaderFields(
            FileLength: BinaryPrimitives.ReadInt64LittleEndian(header[OffFileLength..]),
            TableOffset: BinaryPrimitives.ReadInt64LittleEndian(header[OffTableOffset..]),
            TableLength: BinaryPrimitives.ReadInt32LittleEndian(header[OffTableLength..]),
            SectionCount: BinaryPrimitives.ReadInt32LittleEndian(header[OffSectionCount..]),
            TableHash: BinaryPrimitives.ReadUInt64LittleEndian(header[OffTableHash..]),
            MetaOffset: BinaryPrimitives.ReadInt64LittleEndian(header[OffMetaOffset..]),
            MetaLength: BinaryPrimitives.ReadInt32LittleEndian(header[OffMetaLength..]),
            CreatedUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(header[OffCreatedTicks..]),
            MetaHash: BinaryPrimitives.ReadUInt64LittleEndian(header[OffMetaHash..]),
            Minor: BinaryPrimitives.ReadUInt16LittleEndian(header[OffMinor..]));
    }

    internal readonly record struct HeaderFields(
        long FileLength,
        long TableOffset,
        int TableLength,
        int SectionCount,
        ulong TableHash,
        long MetaOffset,
        int MetaLength,
        long CreatedUtcTicks,
        ulong MetaHash,
        ushort Minor = SnapshotFormat.MinorVersion);

    internal readonly record struct BlockEntry(long Offset, int StoredLength, int RawLength, byte Codec, ulong Hash);

    internal sealed record SectionEntry(SectionKind Kind, int ElementSize, long RawLength, BlockEntry[] Blocks);
}

/// <summary>Section identifiers. Values are stored in files and must never change.</summary>
internal enum SectionKind : ushort
{
    NameChars = 1,
    NameOffsets = 2,

    FsParentDelta = 10,
    FsName = 11,
    FsAttributes = 12,
    FsSize = 13,
    FsLastWrite = 14,
    FsDescendants = 15,
    FsChildFolders = 16,
    FsChildFiles = 17,
    FsDigest = 18,

    /// <summary>NTFS file reference numbers; written only to SysStateTracer.cache (plan §7.8).</summary>
    FsFileId = 19,

    RegParentDelta = 30,
    RegName = 31,
    RegLastWrite = 32,
    RegFirstValue = 33,
    RegValueCount = 34,
    RegDescendants = 35,
    RegDigest = 36,

    ValName = 40,
    ValType = 41,
    ValDataHash = 42,
    ValBlob = 43,
    ValDataLength = 44,

    BlobOffsets = 50,
    BlobData = 51,

    SvcName = 60,
    SvcDisplayName = 61,
    SvcType = 62,
    SvcState = 63,
    SvcStartType = 64,
    SvcFlags = 65,

    SkippedChars = 70,
    SkippedOffsets = 71,
    SkippedErrors = 72,

    /// <summary>UTF-8 JSON describing what an SysStateTracer.cache file was built from; cache files only.</summary>
    CacheInfo = 80,
}

/// <summary>A snapshot file is damaged, truncated, from a newer version, or not a snapshot at all.</summary>
public sealed class SnapshotFormatException : Exception
{
    public SnapshotFormatException()
    {
    }

    public SnapshotFormatException(string message)
        : base(message)
    {
    }

    public SnapshotFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SnapshotFormatException(string path, string reason, Exception? innerException = null)
        : base($"{reason} ({path})", innerException)
    {
        FilePath = path;
        Reason = reason;
    }

    public string? FilePath { get; }

    /// <summary>The problem, without the path.</summary>
    public string? Reason { get; }
}
