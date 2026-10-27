using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>Current metadata of one file or folder, read without opening its contents.</summary>
internal readonly record struct FileMetadata(long FileId, uint Attributes, long Size, long LastWriteTime)
{
    public bool IsDirectory => (Attributes & NativeMethods.FileAttributeDirectory) != 0;
}

/// <summary>
/// NTFS file reference numbers: reading them for a path, and reading a file's metadata by its number
/// (OpenFileById), which is how incremental snapshots refresh files the change journal reports (plan §8.5).
/// Only attributes are requested, with backup semantics, without following reparse points and without
/// recalling cloud files.
/// </summary>
internal static class FileIdentity
{
    private const uint ReadAttributes = 0x0080;
    private const uint OpenFlags = NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint | NativeMethods.FileFlagOpenNoRecall;
    private const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Metadata for <paramref name="path"/>, or null when it cannot be opened.</summary>
    public static FileMetadata? TryRead(string path)
    {
        var open = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + (path.Length == 2 ? path + "\\" : path);
        using var handle = NativeMethods.CreateFile(open, ReadAttributes, ShareAll, 0, FileMode.Open, OpenFlags, 0);
        return handle.IsInvalid ? null : Read(handle);
    }

    /// <summary>Opens the root folder of a drive; used as the "volume hint" for <see cref="TryReadById"/>.</summary>
    public static SafeFileHandle OpenVolumeHint(char drive) =>
        NativeMethods.CreateFile($@"\\?\{drive}:\", ReadAttributes, ShareAll, 0, FileMode.Open, OpenFlags, 0);

    /// <summary>Metadata of the file with reference number <paramref name="fileId"/>, or null (deleted, no access).</summary>
    public static unsafe FileMetadata? TryReadById(SafeFileHandle volumeHint, long fileId)
    {
        // FILE_ID_DESCRIPTOR: DWORD dwSize, FILE_ID_TYPE Type (0 = FileIdType), LARGE_INTEGER FileId (+ padding to 24)
        byte* descriptor = stackalloc byte[24];
        var span = new Span<byte>(descriptor, 24);
        span.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(span, 24);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], fileId);

        using var handle = NativeMethods.OpenFileById(volumeHint, descriptor, ReadAttributes, ShareAll, 0, OpenFlags);
        return handle.IsInvalid ? null : Read(handle);
    }

    private static FileMetadata? Read(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var id = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
        var isDirectory = (info.FileAttributes & NativeMethods.FileAttributeDirectory) != 0;
        var size = isDirectory ? 0 : ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        return new FileMetadata(id, info.FileAttributes, size, info.LastWriteTime);
    }
}
