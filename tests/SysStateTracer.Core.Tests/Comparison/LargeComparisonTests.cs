using System.Diagnostics;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Tests.Storage;
using Xunit.Abstractions;

namespace SysStateTracer.Core.Tests.Comparison;

/// <summary>
/// Plan Step 32 (benchmark 3): comparing 2-million-entry snapshots. Targets (§9.6): a few thousand changes
/// in under 1 s; nearly everything different in 5–10 s. Asserts are loose so CI stays stable; the measured
/// times are printed.
/// </summary>
[Collection(nameof(SerialCollection))]
public class LargeComparisonTests(ITestOutputHelper output)
{
    private const int TopFolders = 100;
    private const int SubFoldersEach = 999;
    private const int FilesPerFolder = 20;

    [Fact]
    public void TwoMillionEntries_FewChanges_AndEverythingChanged()
    {
        var baseline = Build(changeEvery: 0, sizeOffset: 0);
        var few = Build(changeEvery: 500, sizeOffset: 1);
        var all = Build(changeEvery: 1, sizeOffset: 1);

        Compare(baseline, baseline); // warm-up (JIT)

        var (fewTime, fewDiff) = Compare(baseline, few);
        var (allTime, allDiff) = Compare(baseline, all);

        output.WriteLine($"Entries: {baseline.FileSystem.Count:N0} per snapshot");
        output.WriteLine($"Few changes: {fewDiff.FileSystemCounts.Modified:N0} modified, {fewDiff.FileSystem.Count:N0} nodes, {fewTime.TotalMilliseconds:F0} ms (target < 1000 ms)");
        output.WriteLine($"Everything changed: {allDiff.FileSystemCounts.Modified:N0} modified, {allDiff.FileSystem.Count:N0} nodes, {allTime.TotalMilliseconds:F0} ms (target 5-10 s)");

        Assert.Equal(TopFolders * SubFoldersEach * FilesPerFolder / 500, fewDiff.FileSystemCounts.Modified);
        Assert.Equal(TopFolders * SubFoldersEach * FilesPerFolder, allDiff.FileSystemCounts.Modified);
        Assert.True(fewTime < TimeSpan.FromSeconds(5), $"few changes took {fewTime}");
        Assert.True(allTime < TimeSpan.FromSeconds(60), $"everything changed took {allTime}");
    }

    private static (TimeSpan Time, DiffResult Diff) Compare(Snapshot a, Snapshot b)
    {
        var watch = Stopwatch.StartNew();
        var diff = SnapshotComparer.Compare(a, b);
        return (watch.Elapsed, diff);
    }

    /// <summary>The same tree each time; every <paramref name="changeEvery"/>-th file gets a different size.</summary>
    private static Snapshot Build(int changeEvery, long sizeOffset)
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
                    var id = fileCounter++;
                    id.TryFormat(buffer[5..], out var written);
                    "file_".CopyTo(buffer);
                    ".dat".CopyTo(buffer[(5 + written)..]);
                    var size = (id * 37L) % 1_000_000;
                    if (changeEvery > 0 && id % changeEvery == 0)
                    {
                        size += sizeOffset;
                    }

                    fs.Add(sub, buffer[..(9 + written)], 0x20, size, baseTime + id);
                }
            }
        }

        var tree = fs.Build();
        names.Freeze();
        return new Snapshot(new SnapshotMeta { Name = "large" }, names, tree);
    }
}
