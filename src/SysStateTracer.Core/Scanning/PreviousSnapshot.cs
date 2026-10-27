using System.Globalization;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning.Fs.Ntfs;

namespace SysStateTracer.Core.Scanning;

/// <summary>
/// Decides how much of the previous snapshot (SysStateTracer.cache) a new snapshot can reuse
/// (plan §8.5 step 1, §8.6) and keeps what is needed for that. Every reason not to reuse is recorded,
/// so the snapshot's details can say why a part was scanned in full.
/// </summary>
internal sealed class PreviousSnapshot
{
    /// <summary>More changed entries than this share of the previous tree → a full scan is cheaper (plan §8.5 step 8).</summary>
    private const double MaxChangedShare = 0.25;

    private PreviousSnapshot(Snapshot? snapshot, Dictionary<char, UsnChanges>? changes, bool reuseRegistry, string? fileSystemReason, string? registryReason)
    {
        Snapshot = snapshot;
        FileSystemChanges = changes;
        ReuseRegistry = reuseRegistry;
        FileSystemReason = fileSystemReason;
        RegistryReason = registryReason;
    }

    public Snapshot? Snapshot { get; }

    /// <summary>Changes per drive since the previous snapshot; null = scan the file system in full.</summary>
    public Dictionary<char, UsnChanges>? FileSystemChanges { get; }

    public bool ReuseRegistry { get; }

    /// <summary>Why the file system is scanned in full (null when it is not).</summary>
    public string? FileSystemReason { get; }

    public string? RegistryReason { get; }

    /// <summary>Folders the previous scan could not read; kept for unchanged parts of an incremental scan.</summary>
    public IEnumerable<SkippedItem> CarriedSkipped =>
        FileSystemChanges is null || Snapshot is null
            ? []
            : Snapshot.Skipped.Where(s => s.Path.Length >= 2 && s.Path[1] == ':');

    public static PreviousSnapshot None(string reason) => new(null, null, false, reason, reason);

    public static PreviousSnapshot Plan(
        SnapshotRequest request, List<string> fsRoots, List<string> regRoots, List<CacheVolume> checkpoints, CancellationToken cancellationToken)
    {
        if (request.CachePath is null)
        {
            return None("no cache in use");
        }

        if (request.FullRescan)
        {
            return None("full rescan requested");
        }

        var loaded = SnapshotCache.TryLoad(request.CachePath, cancellationToken, out var loadReason);
        if (loaded is not { } cache)
        {
            return None(loadReason);
        }

        var (snapshot, info) = cache;
        if (!string.Equals(info.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return None("the previous snapshot was taken on another computer");
        }

        if (!string.Equals(info.IgnoreFingerprint, request.Ignore.Fingerprint, StringComparison.Ordinal))
        {
            return None("the Global Ignore list changed");
        }

        var registryReason = regRoots.Count == 0 ? null
            : !SameRoots(info.RegistryRoots, regRoots) ? "different registry keys than last time"
            : null;

        Dictionary<char, UsnChanges>? changes = null;
        string? fileSystemReason = null;
        if (fsRoots.Count > 0)
        {
            fileSystemReason = !SameRoots(info.FileSystemRoots, fsRoots) ? "different folders than last time"
                : !snapshot.FileSystem.HasFileIds ? "the previous snapshot has no file ids"
                : null;
            if (fileSystemReason is null)
            {
                changes = ReadChanges(snapshot, info, fsRoots, checkpoints, cancellationToken, out fileSystemReason);
            }
        }

        var reuseRegistry = regRoots.Count > 0 && registryReason is null;
        if (changes is null && !reuseRegistry)
        {
            return new PreviousSnapshot(null, null, false, fileSystemReason, registryReason);
        }

        return new PreviousSnapshot(snapshot, changes, reuseRegistry, fileSystemReason, registryReason);
    }

    /// <summary>One line for the snapshot's details (Meta.ModeReason).</summary>
    public string? Describe(long keysReused, long keysScanned, bool hasFileSystem, bool hasRegistry)
    {
        var parts = new List<string>();
        if (hasFileSystem)
        {
            parts.Add(FileSystemChanges is null ? $"files: full scan ({FileSystemReason})"
                : FileSystemReason is null ? "files: only changes read"
                : $"files: only changes read, except a full scan where {FileSystemReason}");
        }

        if (hasRegistry)
        {
            parts.Add(ReuseRegistry && keysScanned > 0
                ? string.Create(CultureInfo.InvariantCulture, $"registry: values of {keysReused:N0} of {keysScanned:N0} keys reused")
                : $"registry: full scan ({RegistryReason ?? "nothing to reuse"})");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Changes per drive since the previous snapshot. Drives that cannot be read incrementally are left out
    /// (they are scanned in full) and their reasons collected; null when no drive can be.
    /// </summary>
    private static Dictionary<char, UsnChanges>? ReadChanges(
        Snapshot snapshot, CacheInfo info, List<string> fsRoots, List<CacheVolume> checkpoints, CancellationToken cancellationToken, out string? reason)
    {
        var maxRecords = Math.Max(200_000L, (long)(snapshot.FileSystem.Count * MaxChangedShare));
        var result = new Dictionary<char, UsnChanges>();
        var reasons = new List<string>();
        foreach (var drive in fsRoots.Select(r => char.ToUpperInvariant(r[0])).Distinct())
        {
            var key = drive.ToString();
            var then = info.Volumes.FirstOrDefault(v => string.Equals(v.Drive, key, StringComparison.OrdinalIgnoreCase));
            var now = checkpoints.FirstOrDefault(v => string.Equals(v.Drive, key, StringComparison.OrdinalIgnoreCase));
            string? driveReason = null;
            if (then is null || now is null)
            {
                driveReason = $"drive {drive}: has no change journal";
            }
            else if (then.SerialNumber != now.SerialNumber)
            {
                driveReason = $"drive {drive}: is a different volume than last time";
            }
            else if (then.JournalId != now.JournalId)
            {
                driveReason = $"the change journal of {drive}: was recreated";
            }
            else
            {
                var changes = UsnJournal.TryReadChanges(drive, then.JournalId, then.CheckpointUsn, maxRecords, cancellationToken, out driveReason);
                if (changes is not null)
                {
                    result[drive] = changes;
                    continue;
                }
            }

            reasons.Add(driveReason ?? $"drive {drive}: changes could not be read");
        }

        reason = reasons.Count == 0 ? null : string.Join(", ", reasons);
        return result.Count == 0 ? null : result;
    }

    private static bool SameRoots(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
