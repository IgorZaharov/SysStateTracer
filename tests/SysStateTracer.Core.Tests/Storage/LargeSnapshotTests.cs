using System.Diagnostics;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using Xunit.Abstractions;

namespace SysStateTracer.Core.Tests.Storage;

[CollectionDefinition(nameof(SerialCollection), DisableParallelization = true)]
public sealed class SerialCollection
{
}

/// <summary>
/// Plan Steps 9 and 11 targets with ~2 million file-system entries: memory under 150 MB, and
/// write/read timings reported in the test output. Timing asserts are loose so CI stays stable;
/// precise numbers come from the benchmarks (Steps 21 and 32).
/// </summary>
[Collection(nameof(SerialCollection))]
public class LargeSnapshotTests(ITestOutputHelper output)
{
    private const int TopFolders = 100;
    private const int SubFoldersEach = 999;
    private const int FilesPerFolder = 20;
    private const int DistinctFileNames = 900_000;

    [Fact]
    public void TwoMillionEntries_FitMemoryBudget_AndRoundTrip()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var build = Stopwatch.StartNew();
        var snapshot = BuildLargeSnapshot();
        build.Stop();

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var retainedMb = (after - before) / (1024.0 * 1024.0);
        output.WriteLine($"Entries: {snapshot.FileSystem.Count:N0}, distinct names: {snapshot.Names.Count:N0}");
        output.WriteLine($"Build: {build.ElapsedMilliseconds:N0} ms, retained memory: {retainedMb:F1} MB");
        Assert.True(snapshot.FileSystem.Count >= 2_000_000);
        Assert.True(retainedMb < 150, $"Snapshot model uses {retainedMb:F1} MB (budget 150 MB).");

        using var dir = new TempDirectory();
        var path = dir.File("large.stsnap");

        var write = Stopwatch.StartNew();
        SnapshotWriter.Write(path, snapshot);
        write.Stop();

        var read = Stopwatch.StartNew();
        var loaded = SnapshotReader.Load(path);
        read.Stop();

        var sizeMb = new FileInfo(path).Length / (1024.0 * 1024.0);
        output.WriteLine($"Write: {write.ElapsedMilliseconds:N0} ms, read: {read.ElapsedMilliseconds:N0} ms, file: {sizeMb:F1} MB");

        Assert.Equal(snapshot.FileSystem.Count, loaded.FileSystem.Count);
        Assert.Equal(snapshot.FileSystem.Digest[0], loaded.FileSystem.Digest[0]);
        Assert.Equal(snapshot.FileSystem.Size.ToArray(), loaded.FileSystem.Size.ToArray());
        Assert.True(write.Elapsed < TimeSpan.FromSeconds(30), $"Write took {write.Elapsed}.");
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(30), $"Read took {read.Elapsed}.");
    }

    private static Snapshot BuildLargeSnapshot()
    {
        var names = new NameTable(capacity: 1_100_000);
        var fs = new FsTreeBuilder(names, capacity: 2_100_000);
        var baseTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var root = fs.AddRoot("C:", FsTree.DirectoryAttribute, baseTime);

        var fileCounter = 0;
        Span<char> buffer = stackalloc char[32];
        for (var t = 0; t < TopFolders; t++)
        {
            var top = fs.Add(root, $"Folder{t:D3}", FsTree.DirectoryAttribute, 0, baseTime + t);
            for (var s = 0; s < SubFoldersEach; s++)
            {
                var sub = fs.Add(top, $"Sub{s:D4}", FsTree.DirectoryAttribute, 0, baseTime + s);
                for (var f = 0; f < FilesPerFolder; f++)
                {
                    var id = fileCounter++ % DistinctFileNames;
                    id.TryFormat(buffer[5..], out var written);
                    "file_".CopyTo(buffer);
                    ".dat".CopyTo(buffer[(5 + written)..]);
                    fs.Add(sub, buffer[..(9 + written)], 0x20, (id * 37L) % 1_000_000, baseTime + id);
                }
            }
        }

        var tree = fs.Build();
        names.Freeze();
        return new Snapshot(new SnapshotMeta { Name = "large" }, names, tree);
    }
}
