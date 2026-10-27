using System.Text;
using Microsoft.Win32;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.WindowsTests;

/// <summary>
/// Plan Step 27, the equivalence harness: after scripted changes, an incremental snapshot must equal a
/// full one exactly. Also the cache validation rules of Step 22. Needs administrator rights (change journal).
/// </summary>
[Collection(nameof(IncrementalSnapshotTests))]
public class IncrementalSnapshotTests
{
    private const FileSystemScanMethod Listing = FileSystemScanMethod.ListFolders;

    [Fact]
    public void FileSystem_IncrementalEqualsFull()
    {
        if (!ProcessPrivileges.IsElevated)
        {
            return;
        }

        using var data = new TestFolder();
        using var output = new TestFolder();
        var cache = output.Combine("SysStateTracer.cache");

        // Starting state
        data.CreateFile(@"keep\unchanged.txt", 10);
        data.CreateFile(@"keep\grow.txt", 10);
        data.CreateFile(@"keep\attrib.txt", 10);
        data.CreateFile(@"keep\touch.txt", 10);
        data.CreateFile(@"keep\rename-me.txt", 10);
        data.CreateFile(@"keep\delete-me.txt", 10);
        data.CreateFile(@"keep\linked.txt", 10);
        data.CreateFile(@"rename-folder\inner\a.txt", 3);
        data.CreateFile(@"move-folder\inner\b.txt", 4);
        data.CreateFolder("target");
        data.CreateFile(@"delete-folder\x\y.txt", 5);
        data.CreateFile(@"other\untouched.txt", 1);
        for (var i = 0; i < 50; i++)
        {
            data.CreateFile($@"bulk\f{i}.dat", i);
        }

        Junction.CreateHardLink(data.Combine(@"other\link-to-linked.txt"), data.Combine(@"keep\linked.txt"));

        var first = TakeSnapshot(data, output, "first", cache);
        Assert.Equal(SnapshotMode.Full, first.Meta.Mode);

        // Changes of every kind
        File.AppendAllText(data.Combine(@"keep\grow.txt"), "more data");
        File.SetAttributes(data.Combine(@"keep\attrib.txt"), FileAttributes.Hidden | FileAttributes.Archive);
        File.SetLastWriteTimeUtc(data.Combine(@"keep\touch.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.Move(data.Combine(@"keep\rename-me.txt"), data.Combine(@"keep\renamed.txt"));
        File.Delete(data.Combine(@"keep\delete-me.txt"));
        File.AppendAllText(data.Combine(@"keep\linked.txt"), "changed through one link"); // the other link is in an unchanged folder
        Directory.Move(data.Combine("rename-folder"), data.Combine("renamed-folder"));
        Directory.Move(data.Combine("move-folder"), data.Combine(@"target\moved-folder"));
        Directory.Delete(data.Combine("delete-folder"), recursive: true);
        data.CreateFile(@"new-folder\deep\c.txt", 6);
        data.CreateFile(@"keep\new-file.txt", 7);
        File.WriteAllText(data.Combine(@"bulk\f7.dat"), "rewritten");

        var incremental = TakeSnapshot(data, output, "incremental", cache);
        var full = TakeSnapshot(data, output, "full", cachePath: null);

        Assert.Equal(SnapshotMode.Incremental, incremental.Meta.Mode);
        Assert.Contains(incremental.Meta.FileSystemMethods, m => m.Contains("incremental", StringComparison.Ordinal));
        AssertSameFiles(full, incremental, data.Path);

        // The comparison engine on real snapshots: no difference between incremental and full,
        // and every scripted change between the first and the last snapshot.
        Assert.Equal(0, SnapshotComparer.Compare(full, incremental).FileSystem.Count);
        var diff = SnapshotComparer.Compare(first, full);
        var tree = diff.FileSystem;
        var changes = Enumerable.Range(0, tree.Count)
            .Where(n => tree.Change(n) != ChangeKind.Unchanged)
            .Select(n => (Path: diff.FullPath(tree, n)[(data.Path.Length + 1)..], Change: tree.Change(n)))
            .ToHashSet();
        var expected = new[]
        {
            (@"keep\grow.txt", ChangeKind.Modified),
            (@"keep\attrib.txt", ChangeKind.Modified),
            (@"keep\touch.txt", ChangeKind.Modified),
            (@"keep\linked.txt", ChangeKind.Modified),
            (@"other\link-to-linked.txt", ChangeKind.Modified),
            (@"keep\rename-me.txt", ChangeKind.Deleted),
            (@"keep\renamed.txt", ChangeKind.New),
            (@"keep\delete-me.txt", ChangeKind.Deleted),
            (@"keep\new-file.txt", ChangeKind.New),
            ("rename-folder", ChangeKind.Deleted),
            ("renamed-folder", ChangeKind.New),
            ("move-folder", ChangeKind.Deleted),
            (@"target\moved-folder", ChangeKind.New),
            ("delete-folder", ChangeKind.Deleted),
            ("new-folder", ChangeKind.New),
            (@"bulk\f7.dat", ChangeKind.Modified),
        };
        Assert.Empty(expected.Except(changes));
        Assert.Equal(expected.Length, changes.Count);
    }

    [Fact]
    public void Registry_IncrementalEqualsFull()
    {
        using var key = new TestRegistryKey();
        using var output = new TestFolder();
        var cache = output.Combine("SysStateTracer.cache");

        for (var i = 0; i < 20; i++)
        {
            using var child = key.Key.CreateSubKey($"k{i:D2}");
            child.SetValue("same", i);
            child.SetValue("text", $"value {i}");
            child.SetValue("bin", new byte[] { 1, 2, (byte)i }, RegistryValueKind.Binary);
        }

        var first = RegistrySnapshot(key, output, "first", cache);
        Assert.Equal(SnapshotMode.Full, first.Meta.Mode);

        using (var k = key.Key.OpenSubKey("k01", writable: true)!)
        {
            k.SetValue("text", "changed");
        }

        using (var k = key.Key.OpenSubKey("k02", writable: true)!)
        {
            k.DeleteValue("bin");
        }

        using (var k = key.Key.OpenSubKey("k03", writable: true)!)
        {
            k.SetValue("added", 42L, RegistryValueKind.QWord);
        }

        key.Key.DeleteSubKeyTree("k04");
        using (var k = key.Key.CreateSubKey(@"k99\deeper"))
        {
            k.SetValue("fresh", "yes");
        }

        var incremental = RegistrySnapshot(key, output, "incremental", cache);
        var full = RegistrySnapshot(key, output, "full", cachePath: null);

        Assert.Equal(SnapshotMode.Incremental, incremental.Meta.Mode);
        Assert.Contains("reused", incremental.Meta.ModeReason, StringComparison.Ordinal);
        AssertSameRegistry(full, incremental, key.StoredPath);
    }

    [Fact]
    public void Cache_InvalidationReasons()
    {
        using var data = new TestFolder();
        using var output = new TestFolder();
        var cache = output.Combine("SysStateTracer.cache");
        data.CreateFile(@"a\b.txt", 1);

        // No cache yet
        var first = TakeSnapshot(data, output, "first", cache, Listing);
        Assert.Contains("no previous snapshot", first.Meta.ModeReason, StringComparison.Ordinal);

        // Full rescan requested
        var rescan = TakeSnapshot(data, output, "rescan", cache, Listing, fullRescan: true);
        Assert.Equal(SnapshotMode.Full, rescan.Meta.Mode);
        Assert.Contains("full rescan requested", rescan.Meta.ModeReason, StringComparison.Ordinal);

        // Different folders
        var other = SnapshotOrchestrator.Run(new SnapshotRequest
        {
            Name = "other",
            OutputDirectory = output.Path,
            FileSystemRoots = [data.Combine("a")],
            IncludeServices = false,
            CachePath = cache,
        }).Meta;
        Assert.Contains("different folders", other.ModeReason, StringComparison.Ordinal);

        // Changed Global Ignore list
        TakeSnapshot(data, output, "base", cache, Listing);
        var ignored = SnapshotOrchestrator.Run(new SnapshotRequest
        {
            Name = "ignored",
            OutputDirectory = output.Path,
            FileSystemRoots = [data.Path],
            IncludeServices = false,
            Ignore = GlobalIgnoreList.Build([@"FS:C:\NotThere"]),
            CachePath = cache,
        }).Meta;
        Assert.Contains("Global Ignore list changed", ignored.ModeReason, StringComparison.Ordinal);

        // Damaged cache file
        File.WriteAllText(cache, "not a cache");
        var damaged = TakeSnapshot(data, output, "damaged", cache, Listing);
        Assert.Equal(SnapshotMode.Full, damaged.Meta.Mode);
        Assert.Contains("could not be read", damaged.Meta.ModeReason, StringComparison.Ordinal);

        if (!ProcessPrivileges.IsElevated)
        {
            return;
        }

        // Change journal recreated / checkpoint no longer in the journal
        TakeSnapshot(data, output, "good", cache, Listing);
        RewriteCacheVolumes(cache, v => v.JournalId ^= 1);
        Assert.Contains("recreated", TakeSnapshot(data, output, "recreated", cache, Listing).Meta.ModeReason, StringComparison.Ordinal);

        RewriteCacheVolumes(cache, v => v.CheckpointUsn = -1);
        Assert.Contains("no longer holds", TakeSnapshot(data, output, "wrapped", cache, Listing).Meta.ModeReason, StringComparison.Ordinal);

        RewriteCacheVolumes(cache, v => v.SerialNumber ^= 1);
        Assert.Contains("different volume", TakeSnapshot(data, output, "serial", cache, Listing).Meta.ModeReason, StringComparison.Ordinal);
    }

    private static Snapshot TakeSnapshot(
        TestFolder data, TestFolder output, string name, string? cachePath, FileSystemScanMethod method = FileSystemScanMethod.MasterFileTable, bool fullRescan = false)
    {
        var result = SnapshotOrchestrator.Run(new SnapshotRequest
        {
            Name = name,
            OutputDirectory = output.Path,
            FileSystemRoots = [data.Path],
            IncludeServices = false,
            FileSystemMethod = method,
            CachePath = cachePath,
            FullRescan = fullRescan,
        });
        var loaded = SnapshotReader.Load(result.Path);
        return new Snapshot(result.Meta, loaded.Names, loaded.FileSystem, loaded.Registry, loaded.Services, loaded.Skipped);
    }

    private static Snapshot RegistrySnapshot(TestRegistryKey key, TestFolder output, string name, string? cachePath)
    {
        var result = SnapshotOrchestrator.Run(new SnapshotRequest
        {
            Name = name,
            OutputDirectory = output.Path,
            RegistryRoots = [key.HkcuPath],
            IncludeServices = false,
            CachePath = cachePath,
        });
        var loaded = SnapshotReader.Load(result.Path);
        return new Snapshot(result.Meta, loaded.Names, loaded.FileSystem, loaded.Registry, loaded.Services, loaded.Skipped);
    }

    private static void RewriteCacheVolumes(string cache, Action<CacheVolume> change)
    {
        var loaded = SnapshotCache.TryLoad(cache, CancellationToken.None, out var reason) ?? throw new InvalidOperationException(reason);
        foreach (var volume in loaded.Info.Volumes)
        {
            change(volume);
        }

        SnapshotCache.Save(cache, loaded.Snapshot, loaded.Info);
    }

    private static void AssertSameFiles(Snapshot expected, Snapshot actual, string root)
    {
        var e = Entries(expected, root);
        var a = Entries(actual, root);
        var differences = new List<string>();
        foreach (var (path, value) in e)
        {
            if (!a.TryGetValue(path, out var other))
            {
                differences.Add($"missing {path}");
            }
            else if (other != value)
            {
                differences.Add($"{path}: full {value} / incremental {other}");
            }
        }

        differences.AddRange(a.Keys.Where(p => !e.ContainsKey(p)).Select(p => $"extra {p}"));
        Assert.True(differences.Count == 0, string.Join(" | ", differences.Take(15)));
        Assert.True(e.Count > 60, $"{e.Count} entries");
    }

    private static Dictionary<string, (uint Attributes, long Size, long LastWrite)> Entries(Snapshot snapshot, string root)
    {
        var tree = snapshot.FileSystem;
        var result = new Dictionary<string, (uint, long, long)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tree.Count; i++)
        {
            var path = tree.GetFullPath(i, snapshot.Names);
            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                result[path[(root.Length + 1)..]] = (tree.Attributes[i], tree.Size[i], tree.LastWriteTime[i]);
            }
        }

        return result;
    }

    private static void AssertSameRegistry(Snapshot expected, Snapshot actual, string root)
    {
        var e = RegistryEntries(expected, root);
        var a = RegistryEntries(actual, root);
        Assert.Equal(e.Keys.Order(StringComparer.Ordinal), a.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, value) in e)
        {
            Assert.True(a[path] == value, $"{path}: full {value} / incremental {a[path]}");
        }

        Assert.Contains(e.Keys, k => k.EndsWith(@"k99\deeper", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(e.Keys, k => k.EndsWith(@"\k04", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Key path → its values as text (name, type, data hash, length), sorted.</summary>
    private static Dictionary<string, string> RegistryEntries(Snapshot snapshot, string root)
    {
        var tree = snapshot.Registry;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < tree.KeyCount; k++)
        {
            var path = tree.GetFullPath(k, snapshot.Names);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = new StringBuilder();
            var first = tree.FirstValue[k];
            for (var v = first; v < first + tree.KeyValueCount[k]; v++)
            {
                text.Append(snapshot.Names.GetString(tree.ValueName[v])).Append('=')
                    .Append(tree.ValueDataType[v]).Append(':').Append(tree.ValueDataHash[v]).Append(':').Append(tree.ValueDataLength[v]).Append(';');
            }

            result[path] = text.ToString();
        }

        return result;
    }
}
