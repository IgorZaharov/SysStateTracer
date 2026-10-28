using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Benchmarks;

/// <summary>
/// End-to-end comparison timing (plan Step 54, §9.6): loads the two newest snapshots in a folder in parallel,
/// as the comparison window does, compares them, swaps, and builds the exports and the HTML report.
/// <code>compare &lt;folder&gt; [--label text]</code>
/// </summary>
internal static class CompareBenchmark
{
    public static int Run(string[] args)
    {
        var folder = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[0])
            : Path.Combine(Path.GetTempPath(), "SysStateTracerBench");
        var labelIndex = Array.FindIndex(args, a => a.Equals("--label", StringComparison.OrdinalIgnoreCase));
        var label = labelIndex >= 0 && labelIndex + 1 < args.Length ? args[labelIndex + 1] : "compare";

        var newest = new SnapshotCatalog(folder).Newest(2);
        if (newest.Count < 2)
        {
            Console.WriteLine($"RESULT: {label}: needs two snapshots in {folder}, found {newest.Count}");
            return 1;
        }

        var (older, newer) = (newest[1], newest[0]);
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var load = Stopwatch.StartNew();
        var a = Task.Run(() => SnapshotReader.Load(older.Path));
        var b = Task.Run(() => SnapshotReader.Load(newer.Path));
        Task.WaitAll(a, b);
        load.Stop();
        var loaded = GC.GetTotalMemory(forceFullCollection: true) - before;

        var ignore = GlobalIgnoreList.FromText(DefaultIgnoreRules.Text, IgnoreRuleSource.Local).Matcher;
        var compare = Stopwatch.StartNew();
        var diff = SnapshotComparer.Compare(a.Result, b.Result, ignore);
        compare.Stop();

        var swap = Stopwatch.StartNew();
        var swapped = diff.Swap();
        swap.Stop();
        GC.KeepAlive(swapped);

        var export = Stopwatch.StartNew();
        var (reg, _) = DiffExport.Registry(diff, diff.Registry.Roots());
        var files = DiffExport.Files(diff, diff.FileSystem.Roots());
        export.Stop();

        var reportPath = Path.Combine(Path.GetTempPath(), $"SysStateTracerBench-{Guid.NewGuid():N}.html");
        var report = Stopwatch.StartNew();
        HtmlReport.Write(diff, reportPath);
        report.Stop();
        var reportSize = new FileInfo(reportPath).Length;
        File.Delete(reportPath);

        using var process = Process.GetCurrentProcess();
        var fs = diff.FileSystemCounts;
        var r = diff.RegistryCounts;
        var s = diff.ServicesCounts;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RESULT: {label}: files {new FileInfo(older.Path).Length / 1048576.0:F1} + {new FileInfo(newer.Path).Length / 1048576.0:F1} MB; " +
            $"load both {load.ElapsedMilliseconds} ms (target 1-2 s), in memory {loaded / 1048576.0:F0} MB; " +
            $"compare {compare.ElapsedMilliseconds} ms (target < 1 s), {diff.FileSystem.Count + diff.Registry.Count + diff.Services.Count:N0} nodes; " +
            $"changes: files {fs.New:N0}/{fs.Modified:N0}/{fs.Deleted:N0}, registry {r.New:N0}/{r.Modified:N0}/{r.Deleted:N0}, services {s.New:N0}/{s.Modified:N0}/{s.Deleted:N0} (new/modified/deleted); " +
            $"swap {swap.Elapsed.TotalMilliseconds:F2} ms; exports {export.ElapsedMilliseconds} ms ({reg.KeyCount:N0} keys, {files.Count:N0} files); " +
            $"HTML report {report.ElapsedMilliseconds} ms, {reportSize / 1048576.0:F1} MB; peak working set {process.PeakWorkingSet64 / 1048576.0:F0} MB"));
        return 0;
    }
}
