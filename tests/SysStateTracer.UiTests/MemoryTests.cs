using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using Xunit.Abstractions;

namespace SysStateTracer.UiTests;

/// <summary>Issue #9: a closed window must not keep its snapshots and comparison in memory.</summary>
public class MemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void ClosedComparisonWindow_ReleasesItsSnapshotsAndComparison()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var before = app.AddSnapshot("Before Office 2024 install", hoursAgo: 30, extraFiles: 3);
            var after = app.AddSnapshot("After Office 2024 install", hoursAgo: 29, extraFiles: 8, installed: true);

            var start = Collect();
            var (form, diff, left, right) = OpenUseAndClose(app, before, after);
            Ui.Pump(300);
            var end = Collect();
            output.WriteLine($"Managed heap: {start / 1024:N0} KB before, {end / 1024:N0} KB after the window closed");

            Assert.False(form.IsAlive, "the closed ComparisonForm is still referenced");
            Assert.False(diff.IsAlive, "the DiffResult of the closed window is still referenced");
            Assert.False(left.IsAlive, "the left snapshot of the closed window is still referenced");
            Assert.False(right.IsAlive, "the right snapshot of the closed window is still referenced");
        });
    }

    /// <summary>
    /// With snapshots of about a million files and 200,000 registry values: once the window closed and
    /// <see cref="MemoryRelease.Delay"/> passed, the program itself gives the memory back (no collection forced here).
    /// Before issue #9 the GC heap stayed at about 130 MB and the GC kept 300 MB committed.
    /// </summary>
    [Fact]
    public void LargeComparison_MemoryIsGivenBackAfterTheWindowCloses()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var before = WriteLarge(app, "large before", hoursAgo: 2, changed: false);
            var after = WriteLarge(app, "large after", hoursAgo: 1, changed: true);
            Report("idle, before opening");

            var releases = MemoryRelease.Count;
            var (_, diff, _, _) = OpenUseAndClose(app, before, after, measure: () => Report("window open, Show all"));
            Ui.Pump(1000);
            Report("1 s after closing");
            Assert.True(Ui.PumpUntil(() => MemoryRelease.Count > releases, 10_000), "memory was not released after the window closed");
            Ui.Pump(300);
            var committed = Report("after the release");

            Assert.False(diff.IsAlive, "the DiffResult of the closed window is still referenced");
            Assert.True(GC.GetTotalMemory(false) < 64L << 20, $"GC heap still {GC.GetTotalMemory(false) / 1048576} MB");
            Assert.True(committed < 96L << 20, $"GC still has {committed / 1048576} MB committed");
        });

        long Report(string when)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{when,-24} working set {process.WorkingSet64 / 1048576,5:N0} MB  private {process.PrivateMemorySize64 / 1048576,5:N0} MB  GC heap {GC.GetTotalMemory(false) / 1048576,5:N0} MB  GC committed {committed / 1048576,5:N0} MB"));
            return committed;
        }
    }

    [Fact]
    public void ClosingNewSnapshot_ReleasesMemory()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var releases = MemoryRelease.Count;
            using (var form = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
            {
                form.Close();
            }

            Assert.True(Ui.PumpUntil(() => MemoryRelease.Count > releases, 10_000), "memory was not released after New Snapshot closed");
        });
    }

    private static string WriteLarge(TestApp app, string name, double hoursAgo, bool changed)
    {
        var names = new NameTable();
        var fs = new FsTreeBuilder(names);
        var time = new DateTime(2026, 9, 28, 7, 40, 9, DateTimeKind.Utc).ToFileTimeUtc();
        var c = fs.AddRoot("C:", FsTree.DirectoryAttribute, time);
        for (var d = 0; d < 1000; d++)
        {
            var folder = fs.Add(c, $"Folder{d:D4}", FsTree.DirectoryAttribute, 0, time);
            for (var f = 0; f < 1000; f++)
            {
                // Every 20th file in every 2nd folder differs: 25,000 modified files.
                var modified = changed && d % 2 == 0 && f % 20 == 0;
                fs.Add(folder, $"file{f:D4}.dat", 0x20, modified ? 2000 + f : 1000 + f, modified ? time + 1 : time);
            }
        }

        var reg = new RegTreeBuilder(names);
        var hklm = reg.AddRoot("HKLM", time);
        var software = reg.AddKey(hklm, "SOFTWARE", time);
        for (var k = 0; k < 2000; k++)
        {
            var key = reg.AddKey(software, $"Vendor{k:D4}", time);
            for (var v = 0; v < 100; v++)
            {
                var modified = changed && k % 8 == 0;
                reg.AddValue(key, $"Value{v:D3}", 4, BitConverter.GetBytes(modified ? v + 1 : v));
            }
        }

        var svc = new ServiceTableBuilder(names);
        svc.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None);
        var started = DateTimeOffset.Now.AddHours(-hoursAgo);
        var meta = new SnapshotMeta { Name = name, StartedAt = started, CompletedAt = started.AddSeconds(5), ComputerName = "OFFICE-PC" };
        var snapshot = new Snapshot(meta, names, fs.Build(), reg.Build(), svc.Build());
        names.Freeze();
        var path = Path.Combine(app.SnapshotFolder, name + ".stsnap");
        SnapshotWriter.Write(path, snapshot);
        return path;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Form, WeakReference Diff, WeakReference Left, WeakReference Right) OpenUseAndClose(TestApp app, string before, string after, Action? measure = null)
    {
        var form = Ui.Show(new ComparisonForm(app.Paths, app.Settings, before, after));
        Assert.True(Ui.PumpUntil(() => form.Diff is not null && !form.IsLoading, 120_000), "comparison did not load");
        form.ShowAll = true;
        Ui.Pump();
        measure?.Invoke();
        form.Swap();
        Ui.Pump();
        DiffResult diff = form.Diff!;
        var refs = (new WeakReference(form), new WeakReference(diff), new WeakReference(diff.Left), new WeakReference(diff.Right));
        form.Close();
        Ui.Pump();
        form.Dispose();
        return refs;
    }

    private static long Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            Ui.Pump(50);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
