using System.Diagnostics;
using System.Runtime;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Core.Scanning.Reg;
using SysStateTracer.Core.Scanning.Svc;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Scanning;

/// <summary>What to put in a new snapshot and where to write it.</summary>
public sealed class SnapshotRequest
{
    /// <summary>Snapshot name = file name without .stsnap.</summary>
    public required string Name { get; init; }

    /// <summary>Folder the .stsnap file is written to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>e.g. "C:\", "D:\Data". Empty = no file-system part.</summary>
    public IReadOnlyList<string> FileSystemRoots { get; init; } = [];

    /// <summary>e.g. "HKLM", "HKU". Empty = no registry part.</summary>
    public IReadOnlyList<string> RegistryRoots { get; init; } = [];

    public bool IncludeServices { get; init; } = true;

    public bool IncludeDrivers { get; init; }

    public GlobalIgnoreList Ignore { get; init; } = GlobalIgnoreList.Empty;

    /// <summary>Folders listed at the same time; null = chosen from the drive type (plan §8.2).</summary>
    public int? FileSystemParallelism { get; init; }

    /// <summary>Master file table or folder listing (decision D10); automatic by default.</summary>
    public FileSystemScanMethod FileSystemMethod { get; init; } = FileSystemScanMethod.Automatic;

    /// <summary>
    /// SysStateTracer.cache: when set, the snapshot reuses the previous one where nothing changed (plan §8.5,
    /// §8.6) and leaves a new cache behind. Null = always a full scan, no cache.
    /// </summary>
    public string? CachePath { get; init; }

    /// <summary>Ignore the cache and read everything again (plan 2.22); a new cache is still written.</summary>
    public bool FullRescan { get; init; }

    /// <summary>
    /// Default scope (plan D9): all fixed drives, HKLM and HKU, Win32 services.
    /// HKLM\SAM and HKLM\SECURITY are left out by the default Global Ignore rules.
    /// </summary>
    public static SnapshotRequest CreateDefault(string outputDirectory, GlobalIgnoreList ignore) => new()
    {
        Name = SnapshotNaming.DefaultName(),
        OutputDirectory = outputDirectory,
        FileSystemRoots = [.. VolumeDiscovery.GetFixedVolumes().Select(v => v.RootPath)],
        RegistryRoots = ["HKLM", "HKU"],
        IncludeServices = true,
        Ignore = ignore,
    };
}

public sealed record SnapshotResult(string Path, SnapshotMeta Meta);

/// <summary>
/// Takes a full snapshot (plan Step 20): runs the file-system, registry and services scanners at the
/// same time, reports progress, fills the metadata and writes the .stsnap file. Cancelling stops the
/// scanners and leaves no file behind.
/// </summary>
public static class SnapshotOrchestrator
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Called at phase ends ("scanned", "built", "written"); used by the benchmark to sample memory.</summary>
    internal static Action<string>? PhaseHook { get; set; }

    public static SnapshotResult Run(SnapshotRequest request, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SnapshotNaming.IsValidName(request.Name))
        {
            throw new ArgumentException($"'{request.Name}' cannot be used as a snapshot name.", nameof(request));
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var path = Path.Combine(request.OutputDirectory, request.Name + SnapshotNaming.FileExtension);
        if (File.Exists(path))
        {
            throw new IOException($"A snapshot named '{request.Name}' already exists.");
        }

        var total = Stopwatch.StartNew();
        var meta = new SnapshotMeta
        {
            Name = request.Name,
            StartedAt = DateTimeOffset.Now,
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            OsEdition = OsInfo.Edition,
            OsVersion = OsInfo.Version,
            AppVersion = AppInfo.Version,
            Mode = SnapshotMode.Full,
            IgnoreRuleCount = request.Ignore.Matcher.RuleCount,
        };

        progress?.Report(new ScanProgress(SnapshotPhase.Starting, 0, 0, 0, null, null, TimeSpan.Zero));

        // Change-journal checkpoints are taken before anything is read, so whatever changes during
        // the scan is picked up by the next snapshot (plan §8.1).
        var fsRoots = FileSystemScanner.NormalizeRoots(request.FileSystemRoots);
        var regRoots = RegistryScanner.NormalizeRoots(request.RegistryRoots);
        List<CacheVolume> checkpoints = request.CachePath is null ? [] : TakeCheckpoints(fsRoots);

        // Scanning lives in its own method so the builders, scanners and the previous snapshot can be
        // freed before writing.
        var (snapshot, done) = Scan(request, fsRoots, regRoots, checkpoints, meta, total, progress, cancellationToken);
        progress?.Report(done with { Phase = SnapshotPhase.Writing, CurrentPath = null, Elapsed = total.Elapsed });

        meta.CompletedAt = DateTimeOffset.Now;
        meta.Timings.TotalMs = total.ElapsedMilliseconds;
        SnapshotWriter.Write(path, snapshot, cancellationToken: cancellationToken);
        meta.Timings.TotalMs = total.ElapsedMilliseconds;
        PhaseHook?.Invoke("written");

        if (request.CachePath is not null)
        {
            SaveCache(request, fsRoots, regRoots, checkpoints, snapshot);
        }

        progress?.Report(done with { Phase = SnapshotPhase.Done, CurrentPath = null, Elapsed = total.Elapsed });
        return new SnapshotResult(path, meta);
    }

    private static (Snapshot Snapshot, ScanProgress Final) Scan(
        SnapshotRequest request,
        List<string> fsRoots,
        List<string> regRoots,
        List<CacheVolume> checkpoints,
        SnapshotMeta meta,
        Stopwatch total,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var previous = PreviousSnapshot.Plan(request, fsRoots, regRoots, checkpoints, cancellationToken);

        var names = new NameTable(64 * 1024);
        var builderLock = new Lock();
        var skipped = new SkippedCollector();
        var fsBuilder = new FsTreeBuilder(names, 64 * 1024);
        var regBuilder = new RegTreeBuilder(names);
        var svcBuilder = new ServiceTableBuilder(names);
        var fs = new FileSystemScanner(fsBuilder, builderLock, skipped);
        var reg = new RegistryScanner(regBuilder, builderLock, skipped);
        var svc = new ServicesScanner(svcBuilder, builderLock, skipped);

        meta.Scope = [.. fsRoots, .. regRoots];
        foreach (var item in previous.CarriedSkipped)
        {
            skipped.Add(item.Path, item.Error);
        }
        if (request.IncludeServices)
        {
            meta.Scope.Add("Services");
        }

        if (request.IncludeDrivers)
        {
            meta.Scope.Add("Drivers");
        }

        var estimate = EstimateEntries(fsRoots);
        var parallelism = request.FileSystemParallelism ?? ChooseParallelism(fsRoots);

        var tasks = new[]
        {
            Task.Run(
                () => Timed(() =>
                {
                    var options = new FileSystemScanOptions
                    {
                        Roots = fsRoots, Ignore = request.Ignore.Matcher, MaxDegreeOfParallelism = parallelism, Method = request.FileSystemMethod,
                    };
                    if (previous.FileSystemChanges is not null)
                    {
                        fs.ScanIncremental(previous.Snapshot!.FileSystem, previous.Snapshot.Names, previous.FileSystemChanges, options, cancellationToken);
                    }
                    else
                    {
                        fs.Scan(options, cancellationToken);
                    }
                }),
                cancellationToken),
            Task.Run(
                () => Timed(() => reg.Scan(
                    new RegistryScanOptions
                    {
                        Roots = regRoots,
                        Ignore = request.Ignore.Matcher,
                        Previous = previous.ReuseRegistry ? previous.Snapshot!.Registry : null,
                        PreviousNames = previous.ReuseRegistry ? previous.Snapshot!.Names : null,
                    },
                    cancellationToken)),
                cancellationToken),
            Task.Run(
                () => Timed(() => svc.Scan(
                    new ServicesScanOptions { IncludeServices = request.IncludeServices, IncludeDrivers = request.IncludeDrivers, Ignore = request.Ignore.Matcher },
                    cancellationToken)),
                cancellationToken),
        };

        ScanProgress Current() => new(
            SnapshotPhase.Scanning, fs.EntriesScanned, reg.KeysScanned, svc.ServicesScanned, estimate, fs.CurrentPath ?? reg.CurrentPath, total.Elapsed);

        // WaitAny (unlike Wait) does not throw when a scanner fails; Rethrow below picks the real error.
        var all = Task.WhenAll(tasks);
        while (Task.WaitAny([all], ProgressInterval) < 0)
        {
            progress?.Report(Current());
        }

        Rethrow(all, cancellationToken);
        meta.Timings.FileSystemMs = tasks[0].Result;
        meta.Timings.RegistryMs = tasks[1].Result;
        meta.Timings.ServicesMs = tasks[2].Result;
        meta.FileSystemMethods = [.. fs.Methods];
        meta.Mode = previous.FileSystemChanges is not null || reg.KeysReused > 0 ? SnapshotMode.Incremental : SnapshotMode.Full;
        meta.ModeReason = previous.Describe(reg.KeysReused, reg.KeysScanned, fsRoots.Count > 0, regRoots.Count > 0);
        var final = Current();
        previous = PreviousSnapshot.None(string.Empty);
        PhaseHook?.Invoke("scanned");

        // Scanning leaves a lot of short-lived garbage (paths, work items, grown arrays). One compacting
        // collection here lets the build reuse that memory instead of growing the process further.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var fsTree = fsBuilder.Build();
        var regTree = regBuilder.Build();
        var services = svcBuilder.Build();
        names.Freeze();
        PhaseHook?.Invoke("built");
        return (new Snapshot(meta, names, fsTree, regTree, services, skipped.ToList()), final);
    }

    /// <summary>Current change-journal position of every drive in <paramref name="fsRoots"/> that has a journal.</summary>
    private static List<CacheVolume> TakeCheckpoints(List<string> fsRoots)
    {
        var volumes = new List<CacheVolume>();
        foreach (var drive in fsRoots.Select(r => char.ToUpperInvariant(r[0])).Distinct())
        {
            var journal = Fs.Ntfs.UsnJournal.TryQuery(drive, out _);
            var volume = VolumeDiscovery.TryQuery($@"{drive}:\");
            if (journal is not null && volume is not null)
            {
                volumes.Add(new CacheVolume
                {
                    Drive = drive.ToString(),
                    SerialNumber = volume.SerialNumber,
                    JournalId = journal.Value.JournalId,
                    CheckpointUsn = journal.Value.NextUsn,
                });
            }
        }

        return volumes;
    }

    /// <summary>Writes the new SysStateTracer.cache. A failure only costs speed next time, so it is logged, not thrown.</summary>
    private static void SaveCache(SnapshotRequest request, List<string> fsRoots, List<string> regRoots, List<CacheVolume> checkpoints, Snapshot snapshot)
    {
        try
        {
            SnapshotCache.Save(request.CachePath!, snapshot, new CacheInfo
            {
                ComputerName = Environment.MachineName,
                FileSystemRoots = fsRoots,
                RegistryRoots = regRoots,
                IgnoreFingerprint = request.Ignore.Fingerprint,
                Volumes = checkpoints,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Config.Log.Warning($"Could not write the snapshot cache {request.CachePath}", ex);
        }
    }

    private static long Timed(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.ElapsedMilliseconds;
    }

    private static void Rethrow(Task all, CancellationToken cancellationToken)
    {
        if (all.IsCanceled || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The snapshot was stopped.", cancellationToken);
        }

        if (all.IsFaulted && all.Exception is { } ex)
        {
            var real = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(real).Throw();
        }
    }

    /// <summary>Sum of used MFT records for whole drives, for the progress bar.</summary>
    private static long? EstimateEntries(IReadOnlyList<string> roots)
    {
        long sum = 0;
        foreach (var root in roots)
        {
            if (root.Length != 3)
            {
                return null;
            }

            var estimate = VolumeDiscovery.TryQuery(root)?.EstimatedEntries;
            if (estimate is null)
            {
                return null;
            }

            sum += estimate.Value;
        }

        return sum > 0 ? sum : null;
    }

    /// <summary>Spinning disks get 2 workers to avoid seek thrashing; SSDs up to 2× cores (plan §8.2).</summary>
    internal static int ChooseParallelism(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (VolumeDiscovery.TryQuery(root)?.IsSsd == false)
            {
                return 2;
            }
        }

        return Math.Clamp(Environment.ProcessorCount * 2, 2, 16);
    }
}

/// <summary>Windows edition and version for the snapshot metadata.</summary>
internal static class OsInfo
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static string Edition { get; } = ReadEdition();

    public static string Version { get; } = ReadVersion();

    private static string ReadEdition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;

            // Windows 11 still reports "Windows 10" in ProductName.
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }

            return string.IsNullOrEmpty(display) ? product : $"{product} {display}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "Windows";
        }
    }

    private static string ReadVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            var ubr = key?.GetValue("UBR") is int revision ? revision : Environment.OSVersion.Version.Revision;
            var v = Environment.OSVersion.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{ubr}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Environment.OSVersion.Version.ToString();
        }
    }
}
