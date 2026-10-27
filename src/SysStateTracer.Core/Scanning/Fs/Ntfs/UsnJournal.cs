using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs.Ntfs;

/// <summary>A volume's change journal: its id and the range of change numbers (USNs) it still holds.</summary>
internal readonly record struct UsnJournalState(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);

/// <summary>What changed on a volume between two change numbers (plan §8.5 step 4).</summary>
internal sealed class UsnChanges
{
    /// <summary>Files and folders with any change (by file reference number).</summary>
    public HashSet<long> Changed { get; } = [];

    /// <summary>Folders whose list of entries may have changed: the parent of every record, old and new.</summary>
    public HashSet<long> DirtyFolders { get; } = [];

    public long Records { get; set; }
}

/// <summary>
/// Reads the NTFS change journal (FSCTL_QUERY_USN_JOURNAL, FSCTL_READ_USN_JOURNAL). Needs administrator
/// rights. SysStateTracer never creates or resizes a journal: that would itself change the system.
/// </summary>
internal static class UsnJournal
{
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalEntryDeleted = 1181;
    private const int BufferSize = 1024 * 1024;

    public static UsnJournalState? TryQuery(char drive, out string? reason)
    {
        using var volume = Open(drive);
        if (volume.IsInvalid)
        {
            reason = $"cannot open drive {drive}: ({Win32Errors.Describe(Marshal.GetLastPInvokeError())})";
            return null;
        }

        return TryQuery(volume, out reason);
    }

    /// <summary>
    /// Collects the changes with numbers from <paramref name="fromUsn"/> up to the journal's current end.
    /// Returns null with a reason when that range is no longer complete or has more than
    /// <paramref name="maxRecords"/> records (a full scan is then cheaper).
    /// </summary>
    public static unsafe UsnChanges? TryReadChanges(
        char drive, ulong journalId, long fromUsn, long maxRecords, CancellationToken cancellationToken, out string? reason)
    {
        using var volume = Open(drive);
        if (volume.IsInvalid)
        {
            reason = $"cannot open drive {drive}: ({Win32Errors.Describe(Marshal.GetLastPInvokeError())})";
            return null;
        }

        var state = TryQuery(volume, out reason);
        if (state is null)
        {
            return null;
        }

        if (state.Value.JournalId != journalId)
        {
            reason = $"the change journal of {drive}: was recreated";
            return null;
        }

        if (fromUsn < state.Value.FirstUsn || fromUsn < state.Value.LowestValidUsn)
        {
            reason = $"the change journal of {drive}: no longer holds all changes since the last snapshot";
            return null;
        }

        var changes = new UsnChanges();
        var end = state.Value.NextUsn;
        var buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
        try
        {
            // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
            Span<byte> input = stackalloc byte[40];
            var start = fromUsn;
            while (start < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                input.Clear();
                BinaryPrimitives.WriteInt64LittleEndian(input, start);
                BinaryPrimitives.WriteUInt32LittleEndian(input[8..], 0xFFFFFFFF);
                BinaryPrimitives.WriteUInt64LittleEndian(input[32..], journalId);

                int returned;
                fixed (byte* inPtr = input)
                {
                    if (!NativeMethods.DeviceIoControl(volume, FsctlReadUsnJournal, inPtr, input.Length, buffer, BufferSize, out returned, 0))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        reason = error == ErrorJournalEntryDeleted
                            ? $"the change journal of {drive}: no longer holds all changes since the last snapshot"
                            : $"cannot read the change journal of {drive}: ({Win32Errors.Describe(error)})";
                        return null;
                    }
                }

                if (returned < 8)
                {
                    break;
                }

                var data = new ReadOnlySpan<byte>(buffer, returned);
                var next = BinaryPrimitives.ReadInt64LittleEndian(data);
                for (var offset = 8; offset + 8 <= returned;)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    if (length <= 0 || offset + length > returned)
                    {
                        break;
                    }

                    AddRecord(data.Slice(offset, length), changes);
                    offset += length;
                }

                if (changes.Records > maxRecords)
                {
                    reason = $"more than {maxRecords:N0} changes on {drive}: since the last snapshot";
                    return null;
                }

                if (next <= start || returned == 8)
                {
                    break;
                }

                start = next;
            }

            reason = null;
            return changes;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>USN_RECORD_V2 (64-bit ids) or V3 (128-bit ids; the low 64 bits are used, as on NTFS).</summary>
    private static void AddRecord(ReadOnlySpan<byte> record, UsnChanges changes)
    {
        var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        long file;
        long parent;
        if (major == 2 && record.Length >= 60)
        {
            file = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            parent = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
        }
        else if (major == 3 && record.Length >= 76)
        {
            file = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            parent = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        }
        else
        {
            return;
        }

        changes.Records++;
        changes.Changed.Add(file);
        changes.DirtyFolders.Add(parent);
    }

    private static unsafe UsnJournalState? TryQuery(SafeFileHandle volume, out string? reason)
    {
        // USN_JOURNAL_DATA_V0: UsnJournalID, FirstUsn, NextUsn, LowestValidUsn, MaxUsn, MaximumSize, AllocationDelta
        Span<byte> output = stackalloc byte[80];
        fixed (byte* p = output)
        {
            if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FsctlQueryUsnJournal, null, 0, p, output.Length, out var returned, 0) ||
                returned < 32)
            {
                var error = Marshal.GetLastPInvokeError();
                reason = error is ErrorJournalNotActive or ErrorJournalDeleteInProgress
                    ? "the drive has no active change journal"
                    : $"cannot query the change journal ({Win32Errors.Describe(error)})";
                return null;
            }
        }

        reason = null;
        return new UsnJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[24..]));
    }

    private static SafeFileHandle Open(char drive) =>
        NativeMethods.CreateFile(
            $@"\\.\{char.ToUpperInvariant(drive)}:",
            NativeMethods.GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            0,
            0);
}
