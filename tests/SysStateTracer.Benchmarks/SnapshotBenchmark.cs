using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Benchmarks;

/// <summary>
/// Real-system snapshot timing (plan Step 21). Takes one snapshot with the default Global Ignore List
/// and prints "RESULT:" / "INFO:" lines, which CI turns into notices. Run it twice in a row to see a
/// cold and a warm file-system cache; each run is its own process so peak memory is per snapshot.
/// <code>
/// snapshot &lt;output folder&gt; [--label text] [--parallel N] [--only fs|reg|svc] [--roots C:\;D:\Data] [--method auto|mft|list]
///          [--cache file] [--full-rescan]
/// </code>
/// </summary>
internal static class SnapshotBenchmark
{
    public static int Run(string[] args)
    {
        var output = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[0])
            : Path.Combine(Path.GetTempPath(), "SysStateTracerBench");
        var label = Option(args, "--label") ?? "run";
        var parallel = Option(args, "--parallel") is { } text && int.Parse(text, CultureInfo.InvariantCulture) is > 0 and var n ? n : (int?)null;
        var only = Option(args, "--only")?.ToLowerInvariant();
        var method = Option(args, "--method")?.ToLowerInvariant() switch
        {
            "mft" => FileSystemScanMethod.MasterFileTable,
            "list" => FileSystemScanMethod.ListFolders,
            _ => FileSystemScanMethod.Automatic,
        };
        Directory.CreateDirectory(output);

        var ignore = GlobalIgnoreList.FromText(DefaultIgnoreRules.Text, IgnoreRuleSource.Local);
        var defaults = SnapshotRequest.CreateDefault(output, ignore);
        var roots = Option(args, "--roots")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [.. defaults.FileSystemRoots];

        var request = new SnapshotRequest
        {
            Name = $"{label}_{DateTime.Now:yyyyMMdd_HH-mm-ss}",
            OutputDirectory = output,
            FileSystemRoots = only is null or "fs" ? roots : [],
            RegistryRoots = only is null or "reg" ? defaults.RegistryRoots : [],
            IncludeServices = only is null or "svc",
            Ignore = ignore,
            FileSystemParallelism = parallel,
            FileSystemMethod = method,
            CachePath = Option(args, "--cache") is { } cache ? Path.GetFullPath(cache) : null,
            FullRescan = args.Contains("--full-rescan", StringComparer.OrdinalIgnoreCase),
        };

        foreach (var root in request.FileSystemRoots)
        {
            var v = VolumeDiscovery.TryQuery(root);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"INFO: {root} {v?.FileSystem} ssd={v?.IsSsd?.ToString() ?? "unknown"} journal={v?.HasChangeJournal} " +
                $"mft-records={v?.EstimatedEntries:N0} size={v?.TotalBytes / 1073741824.0:F0} GB"));
        }

        var workers = parallel ?? SnapshotOrchestrator.ChooseParallelism(FileSystemScanner.NormalizeRoots(request.FileSystemRoots));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"INFO: {label}: {Environment.ProcessorCount} cores, {workers} folder workers, {ignore.Rules.Count} ignore rules"));

        var watch = Stopwatch.StartNew();
        var lastPrint = TimeSpan.Zero;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == SnapshotPhase.Scanning && p.Elapsed - lastPrint >= TimeSpan.FromSeconds(15))
            {
                lastPrint = p.Elapsed;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Elapsed.TotalSeconds:F0} s: {p.FileSystemEntries:N0} entries, {p.RegistryKeys:N0} keys"));
            }
        });

        // Managed heap and process memory at the end of each phase (not forcing a collection).
        var phases = new List<string>();
        SnapshotOrchestrator.PhaseHook = phase =>
        {
            var info = GC.GetGCMemoryInfo();
            using var self = Process.GetCurrentProcess();
            phases.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{phase}: heap {info.HeapSizeBytes / 1048576.0:F0} MB, committed {info.TotalCommittedBytes / 1048576.0:F0} MB, " +
                $"private {self.PrivateMemorySize64 / 1048576.0:F0} MB, gen2 GCs {GC.CollectionCount(2)}"));
        };

        var result = SnapshotOrchestrator.Run(request, progress);
        SnapshotOrchestrator.PhaseHook = null;
        watch.Stop();

        using var process = Process.GetCurrentProcess();
        var peakWorkingSet = process.PeakWorkingSet64;
        var peakPrivate = process.PeakPagedMemorySize64;
        var size = new FileInfo(result.Path).Length;

        // Memory a fully loaded snapshot keeps (comparison loads two of them).
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var load = Stopwatch.StartNew();
        var loaded = SnapshotReader.Load(result.Path);
        load.Stop();
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(loaded);

        var meta = result.Meta;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RESULT: {label}: total {watch.Elapsed.TotalSeconds:F1} s " +
            $"(fs {meta.Timings.FileSystemMs / 1000.0:F1} s, reg {meta.Timings.RegistryMs / 1000.0:F1} s, " +
            $"svc {meta.Timings.ServicesMs / 1000.0:F1} s, write {meta.Timings.WriteMs / 1000.0:F1} s); " +
            $"{meta.Counts.Folders:N0} folders, {meta.Counts.Files:N0} files, {meta.Counts.RegistryKeys:N0} keys, " +
            $"{meta.Counts.RegistryValues:N0} values ({loaded.Registry.Blobs.Count:N0} distinct, " +
            $"{loaded.Registry.Blobs.Data.Length / 1048576.0:F0} MB data), {meta.Counts.Services:N0} services, {meta.Counts.Skipped:N0} skipped; " +
            $"file {size / 1048576.0:F1} MB; peak working set {peakWorkingSet / 1048576.0:F0} MB, peak private {peakPrivate / 1048576.0:F0} MB; " +
            $"load {load.ElapsedMilliseconds} ms, loaded size {retained / 1048576.0:F0} MB; " +
            $"method: {string.Join(", ", meta.FileSystemMethods)}; mode: {meta.Mode}{(meta.ModeReason is null ? string.Empty : " (" + meta.ModeReason + ")")}"));

        Console.WriteLine($"MEMORY: {label}: " + string.Join("; ", phases));
        var skippedByError = loaded.Skipped
            .GroupBy(i => i.Error)
            .OrderByDescending(g => g.Count())
            .Select(g => $"error {g.Key} x{g.Count()} (e.g. {g.First().Path})");
        Console.WriteLine($"SKIPPED: {label}: " + string.Join("; ", skippedByError));

        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Calls back on the reporting thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
