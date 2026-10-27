using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.WindowsTests;

public class SnapshotOrchestratorTests
{
    [Fact]
    public void Run_WritesSnapshotThatLoadsBack()
    {
        using var data = new TestFolder();
        using var output = new TestFolder();
        using var key = new TestRegistryKey();
        data.CreateFile(@"docs\a.txt", 42);
        data.CreateFile(@"docs\skip.tmp", 1);
        key.Key.SetValue("answer", 42);
        var ignore = GlobalIgnoreList.Build([$@"FS:{data.Path}\*.tmp"]);
        var reports = new List<ScanProgress>();

        var request = new SnapshotRequest
        {
            Name = "test_" + Guid.NewGuid().ToString("N")[..8],
            OutputDirectory = output.Path,
            FileSystemRoots = [data.Path],
            RegistryRoots = [key.HkcuPath],
            IncludeServices = true,
            Ignore = ignore,
        };

        var result = SnapshotOrchestrator.Run(request, new SyncProgress(reports.Add));

        Assert.Equal(Path.Combine(output.Path, request.Name + SnapshotNaming.FileExtension), result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Empty(Directory.GetFiles(output.Path, "*.tmp"));
        Assert.Equal(SnapshotPhase.Done, reports[^1].Phase);
        Assert.Equal(SnapshotPhase.Starting, reports[0].Phase);

        var snapshot = SnapshotReader.Load(result.Path);
        var meta = snapshot.Meta;
        Assert.Equal(request.Name, meta.Name);
        Assert.Equal(Environment.MachineName, meta.ComputerName);
        Assert.StartsWith("Windows", meta.OsEdition, StringComparison.Ordinal);
        Assert.Matches(@"^10\.0\.\d+\.\d+$", meta.OsVersion);
        Assert.Equal(SnapshotMode.Full, meta.Mode);
        Assert.Equal(1, meta.IgnoreRuleCount);
        Assert.Contains(key.StoredPath, meta.Scope, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Services", meta.Scope);
        Assert.True(meta.CompletedAt >= meta.StartedAt);
        Assert.True(meta.Counts.Services > 100);
        Assert.Equal(1, meta.Counts.Files);

        var files = Enumerable.Range(0, snapshot.FileSystem.Count)
            .Select(i => snapshot.FileSystem.GetFullPath(i, snapshot.Names))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(data.Path, @"docs\a.txt"), files);
        Assert.DoesNotContain(Path.Combine(data.Path, @"docs\skip.tmp"), files);

        var keys = Enumerable.Range(0, snapshot.Registry.KeyCount)
            .Select(i => snapshot.Registry.GetFullPath(i, snapshot.Names))
            .ToList();
        Assert.Contains(key.StoredPath, keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, snapshot.Registry.ValueCount);
    }

    [Fact]
    public void Run_RefusesExistingName()
    {
        using var output = new TestFolder();
        var request = new SnapshotRequest { Name = "dup", OutputDirectory = output.Path, IncludeServices = false };
        SnapshotOrchestrator.Run(request);

        Assert.Throws<IOException>(() => SnapshotOrchestrator.Run(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad|name")]
    [InlineData("CON")]
    public void Run_RefusesInvalidName(string name)
    {
        using var output = new TestFolder();

        Assert.Throws<ArgumentException>(() => SnapshotOrchestrator.Run(new SnapshotRequest { Name = name, OutputDirectory = output.Path }));
        Assert.Empty(Directory.GetFiles(output.Path));
    }

    [Fact]
    public void Run_CancelledLeavesNoFile()
    {
        using var data = new TestFolder();
        using var output = new TestFolder();
        data.CreateFile(@"a\b.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SnapshotOrchestrator.Run(
            new SnapshotRequest { Name = "cancelled", OutputDirectory = output.Path, FileSystemRoots = [data.Path] },
            cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(output.Path));
    }

    [Fact]
    public void Run_CancelDuringScanStopsQuickly()
    {
        using var output = new TestFolder();
        using var cts = new CancellationTokenSource();
        var request = new SnapshotRequest
        {
            Name = "cancel_mid",
            OutputDirectory = output.Path,
            FileSystemRoots = [Environment.GetFolderPath(Environment.SpecialFolder.Windows)],
            RegistryRoots = ["HKLM\\SOFTWARE"],
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new SyncProgress(p =>
        {
            if (p.FileSystemEntries > 2000)
            {
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => SnapshotOrchestrator.Run(request, progress, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.Empty(Directory.GetFiles(output.Path));
    }

    [Fact]
    public void CreateDefault_CoversFixedDrivesRegistryAndServices()
    {
        var request = SnapshotRequest.CreateDefault(@"C:\out", GlobalIgnoreList.Empty);

        Assert.True(SnapshotNaming.IsValidName(request.Name));
        Assert.EndsWith("_" + Environment.MachineName, request.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetPathRoot(Environment.SystemDirectory)!.ToUpperInvariant(), request.FileSystemRoots);
        Assert.Equal(new[] { "HKLM", "HKU" }, request.RegistryRoots);
        Assert.True(request.IncludeServices);
        Assert.False(request.IncludeDrivers);
    }

    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
