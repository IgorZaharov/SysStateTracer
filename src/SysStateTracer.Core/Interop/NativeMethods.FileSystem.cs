using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysStateTracer.Core.Interop;

/// <summary>Files, directories and volumes.</summary>
internal static partial class NativeMethods
{
    internal const uint FileListDirectory = 0x0001;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint GenericRead = 0x80000000;
    internal const uint Synchronize = 0x00100000;

    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileFlagNoBuffering = 0x20000000;

    /// <summary>Do not bring cloud placeholders back from remote storage when opening them.</summary>
    internal const uint FileFlagOpenNoRecall = 0x00100000;
    internal const uint FileFlagSequentialScan = 0x08000000;

    internal const uint FileAttributeDirectory = 0x0010;
    internal const uint FileAttributeReparsePoint = 0x0400;
    internal const uint FileAttributeRecallOnOpen = 0x00040000;
    internal const uint FileAttributeRecallOnDataAccess = 0x00400000;

    // FILE_INFO_BY_HANDLE_CLASS
    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileFullDirectoryInfo = 14;

    // DeviceIoControl codes
    internal const uint FsctlQueryUsnJournal = 0x000900F4;
    internal const uint FsctlGetNtfsVolumeData = 0x00090064;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>Synchronous read at the offset given in <paramref name="overlapped"/>.</summary>
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(SafeFileHandle file, byte* buffer, int bytesToRead, out int bytesRead, NativeOverlapped* overlapped);

    /// <summary>BY_HANDLE_FILE_INFORMATION (52 bytes; FILETIME fields are only 4-byte aligned).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary><paramref name="fileIdDescriptor"/>: FILE_ID_DESCRIPTOR (24 bytes).</summary>
    [LibraryImport(Kernel32, SetLastError = true)]
    internal static unsafe partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, byte* fileIdDescriptor, uint desiredAccess, FileShare shareMode, nint securityAttributes, uint flagsAndAttributes);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, int bufferLength);

    /// <summary>
    /// "\\?\Volume{GUID}\" of the volume mounted at <paramref name="folder"/> ("C:\Users\Alex", "D:"), or null when
    /// no volume is mounted there (an ordinary folder or a junction to another folder).
    /// </summary>
    internal static unsafe string? GetMountedVolume(string folder)
    {
        var mountPoint = folder.EndsWith('\\') ? folder : folder + "\\";
        var buffer = new char[64]; // "\\?\Volume{GUID}\" is 49 characters
        bool ok;
        fixed (char* p = buffer)
        {
            ok = GetVolumeNameForVolumeMountPoint(mountPoint, p, buffer.Length);
        }

        if (!ok)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, '\0');
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    [LibraryImport(Kernel32, EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int GetLongPathName(string shortPath, char* longPath, int bufferLength);

    /// <summary>"C:\Users\RUNNER~1\x" → "C:\Users\runneradmin\x"; the input unchanged if it does not exist.</summary>
    internal static unsafe string GetLongPath(string path)
    {
        var prefixed = path.Length >= 248 && !path.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + path : path;
        var buffer = new char[Math.Max(260, prefixed.Length + 64)];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            int length;
            fixed (char* p = buffer)
            {
                length = GetLongPathName(prefixed, p, buffer.Length);
            }

            if (length == 0)
            {
                return path;
            }

            if (length < buffer.Length)
            {
                var result = new string(buffer, 0, length);
                return ReferenceEquals(prefixed, path) ? result : result[4..];
            }

            buffer = new char[length + 1];
        }

        return path;
    }

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, int bufferSize);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        void* inBuffer,
        int inBufferSize,
        void* outBuffer,
        int outBufferSize,
        out int bytesReturned,
        nint overlapped);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumeInformation(
        string rootPathName,
        char* volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        int fileSystemNameSize);

    /// <summary>STORAGE_PROPERTY_QUERY for StorageDeviceSeekPenaltyProperty.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    internal const int StorageDeviceSeekPenaltyProperty = 7;
    internal const int PropertyStandardQuery = 0;
}
