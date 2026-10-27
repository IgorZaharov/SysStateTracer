using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Scanning;

/// <summary>Thread-safe list of paths that could not be read.</summary>
public sealed class SkippedCollector
{
    private readonly Lock _gate = new();
    private readonly List<SkippedItem> _items = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Add(string path, int error)
    {
        lock (_gate)
        {
            _items.Add(new SkippedItem(path, error));
        }
    }

    public List<SkippedItem> ToList()
    {
        lock (_gate)
        {
            return [.. _items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)];
        }
    }
}

/// <summary>Where the snapshot work currently is; shown in the New Snapshot window and by /new-snap.</summary>
public enum SnapshotPhase
{
    Starting,
    Scanning,
    Writing,
    Done,
}

/// <summary>Progress report, sent a few times per second.</summary>
/// <param name="EstimatedFileSystemEntries">Upper estimate for the progress bar, when known.</param>
/// <param name="CurrentPath">A folder or key being read right now.</param>
public sealed record ScanProgress(
    SnapshotPhase Phase,
    long FileSystemEntries,
    long RegistryKeys,
    long Services,
    long? EstimatedFileSystemEntries,
    string? CurrentPath,
    TimeSpan Elapsed)
{
    /// <summary>0–1 for the progress bar, or null when there is no estimate.</summary>
    public double? Fraction => Phase switch
    {
        SnapshotPhase.Done => 1,
        SnapshotPhase.Writing => 0.97,
        _ when EstimatedFileSystemEntries is > 0 => Math.Min(0.95, FileSystemEntries / (double)EstimatedFileSystemEntries.Value),
        _ => null,
    };
}
