using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>A drive that can be scanned (plan Step 16).</summary>
/// <param name="Name">Drive name as stored in snapshots, e.g. "C:".</param>
/// <param name="RootPath">e.g. "C:\".</param>
/// <param name="IsSsd">True = no seek penalty (SSD/NVMe), false = HDD, null = unknown.</param>
/// <param name="ChangeJournalId">USN journal id when the journal is active (needs administrator rights).</param>
/// <param name="EstimatedEntries">Used MFT records on NTFS: an upper estimate of files + folders, for progress.</param>
public sealed record VolumeInfo(
    string Name,
    string RootPath,
    DriveType DriveType,
    string FileSystem,
    string Label,
    uint SerialNumber,
    long TotalBytes,
    long FreeBytes,
    bool? IsSsd,
    ulong? ChangeJournalId,
    long? EstimatedEntries)
{
    public bool HasChangeJournal => ChangeJournalId is not null;
}

/// <summary>Finds fixed drives and their properties.</summary>
public static class VolumeDiscovery
{
    /// <summary>All ready fixed drives (the default scope, plan D9).</summary>
    public static IReadOnlyList<VolumeInfo> GetFixedVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                var info = TryQuery(drive.Name);
                if (info is not null)
                {
                    volumes.Add(info);
                }
            }
        }

        return volumes;
    }

    /// <summary>Properties of the drive containing <paramref name="path"/>, or null if it is not ready.</summary>
    public static VolumeInfo? TryQuery(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        root = char.ToUpperInvariant(root[0]) + @":\";
        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            return null;
        }

        var (serial, fileSystem) = ReadVolumeInformation(root);
        bool? ssd = null;
        ulong? journal = null;
        long? estimate = null;

        using (var device = OpenVolume(root[0], 0))
        {
            if (!device.IsInvalid)
            {
                ssd = QueryNoSeekPenalty(device);
            }
        }

        using (var volume = OpenVolume(root[0], NativeMethods.GenericRead))
        {
            if (!volume.IsInvalid)
            {
                journal = QueryJournalId(volume);
                estimate = QueryMftRecords(volume);
            }
        }

        return new VolumeInfo(
            Name: root[..2],
            RootPath: root,
            DriveType: drive.DriveType,
            FileSystem: fileSystem ?? drive.DriveFormat,
            Label: drive.VolumeLabel,
            SerialNumber: serial,
            TotalBytes: drive.TotalSize,
            FreeBytes: drive.TotalFreeSpace,
            IsSsd: ssd,
            ChangeJournalId: journal,
            EstimatedEntries: estimate);
    }

    /// <summary>Opens \\.\X: — access 0 works for anyone; GENERIC_READ needs administrator rights.</summary>
    internal static SafeFileHandle OpenVolume(char letter, uint access) =>
        NativeMethods.CreateFile(
            $@"\\.\{letter}:",
            access,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            0,
            0);

    private static unsafe (uint Serial, string? FileSystem) ReadVolumeInformation(string root)
    {
        char* fsName = stackalloc char[64];
        if (NativeMethods.GetVolumeInformation(root, null, 0, out var serial, out _, out _, fsName, 64))
        {
            return (serial, new string(fsName));
        }

        return (0, null);
    }

    private static unsafe bool? QueryNoSeekPenalty(SafeFileHandle device)
    {
        var query = new NativeMethods.StoragePropertyQuery
        {
            PropertyId = NativeMethods.StorageDeviceSeekPenaltyProperty,
            QueryType = NativeMethods.PropertyStandardQuery,
        };

        // DEVICE_SEEK_PENALTY_DESCRIPTOR: DWORD Version, DWORD Size, BOOLEAN IncursSeekPenalty
        Span<byte> output = stackalloc byte[12];
        fixed (byte* outPtr = output)
        {
            if (!NativeMethods.DeviceIoControl(
                    device, NativeMethods.IoctlStorageQueryProperty, &query, sizeof(NativeMethods.StoragePropertyQuery),
                    outPtr, output.Length, out var returned, 0) || returned < 9)
            {
                return null;
            }
        }

        return output[8] == 0;
    }

    /// <summary>USN_JOURNAL_DATA_V0.UsnJournalID, or null when there is no active journal.</summary>
    internal static unsafe ulong? QueryJournalId(SafeFileHandle volume)
    {
        Span<byte> output = stackalloc byte[80];
        fixed (byte* outPtr = output)
        {
            if (!NativeMethods.DeviceIoControl(
                    volume, NativeMethods.FsctlQueryUsnJournal, null, 0, outPtr, output.Length, out var returned, 0) || returned < 8)
            {
                return null;
            }
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(output);
    }

    /// <summary>NTFS_VOLUME_DATA_BUFFER: MftValidDataLength / BytesPerFileRecordSegment.</summary>
    private static unsafe long? QueryMftRecords(SafeFileHandle volume)
    {
        Span<byte> output = stackalloc byte[128];
        fixed (byte* outPtr = output)
        {
            if (!NativeMethods.DeviceIoControl(
                    volume, NativeMethods.FsctlGetNtfsVolumeData, null, 0, outPtr, output.Length, out var returned, 0) || returned < 64)
            {
                return null;
            }
        }

        var bytesPerRecord = BinaryPrimitives.ReadUInt32LittleEndian(output[48..]);
        var mftValidDataLength = BinaryPrimitives.ReadInt64LittleEndian(output[56..]);
        return bytesPerRecord == 0 ? null : mftValidDataLength / bytesPerRecord;
    }
}
