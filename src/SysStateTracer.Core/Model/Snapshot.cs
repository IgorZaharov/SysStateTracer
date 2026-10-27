namespace SysStateTracer.Core.Model;

/// <summary>A path that could not be read during the scan, with the Win32 error code.</summary>
public readonly record struct SkippedItem(string Path, int Error);

/// <summary>Which parts of a snapshot to load (plan §9.1: registry data is loaded only when needed).</summary>
[Flags]
public enum SnapshotParts
{
    None = 0,
    FileSystem = 1,
    Registry = 2,

    /// <summary>Registry value data; requires <see cref="Registry"/>.</summary>
    RegistryData = 4,
    Services = 8,
    Skipped = 16,
    All = FileSystem | Registry | RegistryData | Services | Skipped,
}

/// <summary>One snapshot in memory: metadata plus its file-system, registry and services parts.</summary>
public sealed class Snapshot
{
    public Snapshot(
        SnapshotMeta meta,
        NameTable names,
        FsTree? fileSystem = null,
        RegTree? registry = null,
        ServiceTable? services = null,
        IReadOnlyList<SkippedItem>? skipped = null,
        SnapshotParts loadedParts = SnapshotParts.All)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(names);
        Meta = meta;
        Names = names;
        FileSystem = fileSystem ?? FsTree.Empty;
        Registry = registry ?? RegTree.Empty;
        Services = services ?? ServiceTable.Empty;
        Skipped = skipped ?? [];
        LoadedParts = loadedParts;
    }

    public SnapshotMeta Meta { get; }

    /// <summary>Names used by every part.</summary>
    public NameTable Names { get; }

    public FsTree FileSystem { get; }

    public RegTree Registry { get; }

    public ServiceTable Services { get; }

    public IReadOnlyList<SkippedItem> Skipped { get; }

    /// <summary>Parts that were actually loaded; the others are empty.</summary>
    public SnapshotParts LoadedParts { get; }
}
