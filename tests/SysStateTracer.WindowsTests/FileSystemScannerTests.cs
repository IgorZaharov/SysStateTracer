using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Fs;

namespace SysStateTracer.WindowsTests;

public class FileSystemScannerTests
{
    [Fact]
    public void Scan_MatchesReferenceListing()
    {
        using var folder = new TestFolder();
        folder.CreateFile("a.txt", 10);
        folder.CreateFile("no extension", 1);
        folder.CreateFile("Ünïcødé ✓ name.dat", 300);
        folder.CreateFile(@"sub\hidden.bin", 4096, FileAttributes.Hidden);
        folder.CreateFile(@"sub\readonly.txt", 5, FileAttributes.ReadOnly);
        folder.CreateFolder(@"sub\empty");
        for (var i = 0; i < 40; i++)
        {
            folder.CreateFile($@"many\d{i % 7}\f{i}.log", i * 13);
        }

        var deep = string.Join('\\', Enumerable.Range(0, 12).Select(i => $"level{i}"));
        folder.CreateFile(deep + @"\bottom.txt", 1);

        // NTFS copies a folder's times into its parent's listing lazily (when the folder is next opened),
        // so the first listing after creating files can show slightly older folder times. List once first.
        _ = Directory.EnumerateFileSystemEntries(folder.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Count();

        var scan = ScanResult.Run([folder.Path]);

        var reference = new DirectoryInfo(folder.Path)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(i => Path.GetRelativePath(folder.Path, i.FullName), StringComparer.OrdinalIgnoreCase);

        var scanned = scan.Below(folder.Path);
        Assert.Equal(reference.Keys.Order(StringComparer.OrdinalIgnoreCase), scanned.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (relative, info) in reference)
        {
            var entry = scanned[relative];
            Assert.Equal((uint)info.Attributes, scan.Tree.Attributes[entry]);
            Assert.Equal(info.LastWriteTimeUtc.ToFileTimeUtc(), scan.Tree.LastWriteTime[entry]);
            if (info is FileInfo file)
            {
                Assert.Equal(file.Length, scan.Tree.Size[entry]);
                Assert.False(scan.Tree.IsDirectory(entry));
            }
            else
            {
                Assert.True(scan.Tree.IsDirectory(entry));
            }
        }

        Assert.Equal(0, scan.Skipped.Count);
    }

    [Fact]
    public void Scan_AddsFoldersLeadingToTheRoot()
    {
        using var folder = new TestFolder();
        folder.CreateFile(@"x\y.txt");

        var scan = ScanResult.Run([folder.Combine("x")]);

        var drive = folder.Path[..2].ToUpperInvariant();
        var root = Assert.Single(Enumerable.Range(0, scan.Tree.Count), i => scan.Tree.Parent[i] < 0);
        Assert.Equal(drive + "\\", scan.Tree.GetFullPath(root, scan.Names));
        Assert.Contains(scan.Paths.Keys, p => p.Equals(folder.Combine(@"x\y.txt"), StringComparison.OrdinalIgnoreCase));

        // Only the chain to the root is added, not its siblings.
        Assert.Equal(folder.Path.Count(c => c == '\\') + 3, scan.Tree.Count);
    }

    [Fact]
    public void Scan_FollowsLinksOnlyOutsideTheScannedFolders()
    {
        using var folder = new TestFolder();
        var outside = folder.CreateFolder("share");                      // stands in for a network share
        folder.CreateFile(@"share\on the share.txt", 3);
        folder.CreateFile(@"share\sub\deeper.txt", 4);
        folder.CreateFolder(@"root\inner");
        folder.CreateFile(@"root\inner\inside.txt");
        Junction.Create(folder.Combine(@"root\Desktop"), outside);         // redirected profile folder (issue #2)
        Junction.Create(folder.Combine(@"root\again"), folder.Combine(@"root\inner"));   // inside: read once only
        Junction.Create(folder.Combine(@"share\loop"), folder.Combine("root"));         // back into the scan: not followed

        var scan = ScanResult.Run([folder.Combine("root")]);

        // Behind the link to outside: recorded under the link's own path.
        var desktop = scan.Paths[folder.Combine(@"root\Desktop")];
        Assert.NotEqual(0u, scan.Tree.Attributes[desktop] & (uint)FileAttributes.ReparsePoint);
        Assert.Equal(3, scan.Tree.Size[scan.Paths[folder.Combine(@"root\Desktop\on the share.txt")]]);
        Assert.Equal(4, scan.Tree.Size[scan.Paths[folder.Combine(@"root\Desktop\sub\deeper.txt")]]);
        Assert.Equal(0, scan.Tree.Descendants[scan.Paths[folder.Combine(@"root\Desktop\loop")]]);

        // A link to a folder that is scanned anyway is recorded, not followed.
        Assert.Equal(0, scan.Tree.Descendants[scan.Paths[folder.Combine(@"root\again")]]);
        Assert.Single(scan.Paths.Keys, p => p.EndsWith("inside.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForeignFolders_OrdinaryFoldersAreOnTheirOwnDrive()
    {
        using var folder = new TestFolder();
        var inner = folder.CreateFolder(@"Users\Alex");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Empty(FileSystemScanner.ForeignFolders([folder.Path, windows[..3]], [inner, windows, folder.Path]));
        Assert.False(FileSystemScanner.IsOnOtherVolume(inner));
        Assert.NotNull(FileSystemScanner.VolumeSerialOf(windows[..3]));
        Assert.Equal(FileSystemScanner.VolumeSerialOf(windows[..3]), FileSystemScanner.VolumeSerialOf(windows));
    }

    [Fact]
    public void Scan_ReadsPathsLongerThan260Characters()
    {
        using var folder = new TestFolder();
        var segment = new string('d', 60);
        var relative = string.Join('\\', Enumerable.Repeat(segment, 6)) + @"\long.txt";
        var full = folder.CreateFile(relative, 7);
        Assert.True(full.Length > 300);

        var scan = ScanResult.Run([folder.Path]);

        var entry = scan.Paths[full];
        Assert.Equal(7, scan.Tree.Size[entry]);
        Assert.Equal(0, scan.Skipped.Count);
    }

    [Fact]
    public void Scan_LeavesOutIgnoredEntries()
    {
        using var folder = new TestFolder();
        folder.CreateFile(@"keep\a.txt");
        folder.CreateFile(@"skipme\b.txt");
        folder.CreateFile(@"keep\c.log");
        folder.CreateFile(@"keep\deeper\d.log");
        var ignore = GlobalIgnoreList.Build([$@"FS:{folder.Path}\skipme", $@"FS:{folder.Path}\*.log"]);
        Assert.Empty(ignore.Errors);

        var scan = ScanResult.Run([folder.Path], ignore.Matcher);

        var names = scan.Below(folder.Path).Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(new[] { "keep", @"keep\a.txt", @"keep\deeper" }, names);
    }

    [Fact]
    public void Scan_MissingRootIsSkipped()
    {
        using var folder = new TestFolder();
        var missing = folder.Combine("does-not-exist");

        var scan = ScanResult.Run([missing]);

        var skipped = Assert.Single(scan.Skipped.ToList());
        Assert.Equal(missing, skipped.Path, ignoreCase: true);
    }

    [Fact]
    public void Scan_StopsWhenCancelled()
    {
        using var folder = new TestFolder();
        folder.CreateFile(@"a\b.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ScanResult.Run([folder.Path], cancellationToken: cts.Token));
    }

    [Fact]
    public void Scan_ReadsSystemFolder()
    {
        // Protected folders are readable thanks to backup semantics; this one is readable by everyone anyway.
        var drivers = Path.Combine(Environment.SystemDirectory, "drivers");

        var scan = ScanResult.Run([drivers]);

        Assert.Contains(scan.Paths.Keys, p => p.Equals(Path.Combine(drivers, @"etc\hosts"), StringComparison.OrdinalIgnoreCase));
        Assert.True(scan.Tree.Count > 50, $"{scan.Tree.Count} entries");
    }

    [Fact]
    public void FileTable_MatchesFolderListing()
    {
        using var folder = new TestFolder();
        folder.CreateFile("a.txt", 10);
        folder.CreateFile("Ünïcødé ✓ name.dat", 300);
        folder.CreateFile("resident.txt", 20);            // small enough to live inside its file record
        folder.CreateFile(@"big\large.bin", 3_000_000);   // data outside the record
        folder.CreateFile(@"sub\hidden.bin", 4096, FileAttributes.Hidden);
        folder.CreateFile(@"sub\readonly.txt", 5, FileAttributes.ReadOnly);
        folder.CreateFolder(@"sub\empty");
        File.WriteAllText(folder.Combine("a.txt") + ":extra", "alternate stream not counted in the size");
        Junction.CreateHardLink(folder.Combine(@"sub\second name.txt"), folder.Combine("a.txt"));
        Junction.Create(folder.Combine("junction"), folder.Combine("big"));
        for (var i = 0; i < 300; i++)
        {
            folder.CreateFile($@"many\d{i % 9}\file with a longer name number {i}.log", i);
        }

        var deep = string.Join('\\', Enumerable.Repeat(new string('d', 60), 6));
        folder.CreateFile(deep + @"\long.txt", 7);

        // List once first so NTFS brings its folder listings up to date (times are copied lazily).
        _ = ScanResult.Run([folder.Path], method: FileSystemScanMethod.ListFolders);
        var listed = ScanResult.Run([folder.Path], method: FileSystemScanMethod.ListFolders);
        var table = ScanResult.Run([folder.Path], method: FileSystemScanMethod.MasterFileTable);

        if (!SysStateTracer.Core.Interop.ProcessPrivileges.IsElevated)
        {
            Assert.Contains(table.Methods, m => m.Contains("folder listing", StringComparison.Ordinal));
            return;
        }

        Assert.Contains(table.Methods, m => m.EndsWith("master file table", StringComparison.Ordinal));
        var expected = listed.Below(folder.Path);
        var actual = table.Below(folder.Path);
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Keys.Order(StringComparer.OrdinalIgnoreCase));

        var differences = new List<string>();
        foreach (var (path, e) in expected)
        {
            var a = actual[path];
            if (listed.Tree.Attributes[e] != table.Tree.Attributes[a] ||
                listed.Tree.Size[e] != table.Tree.Size[a] ||
                listed.Tree.LastWriteTime[e] != table.Tree.LastWriteTime[a])
            {
                differences.Add(
                    $"{path}: attributes {listed.Tree.Attributes[e]:X}/{table.Tree.Attributes[a]:X}, " +
                    $"size {listed.Tree.Size[e]}/{table.Tree.Size[a]}, time {listed.Tree.LastWriteTime[e]}/{table.Tree.LastWriteTime[a]}");
            }
        }

        Assert.True(differences.Count == 0, "listing/file table: " + string.Join(" | ", differences.Take(12)));
        Assert.Equal(3_000_000, table.Tree.Size[actual[@"big\large.bin"]]);
        Assert.Equal(0, table.Tree.Descendants[actual["junction"]]);
    }

    [Fact]
    public void NormalizeRoots_FixesFormAndDropsNestedRoots()
    {
        var roots = FileSystemScanner.NormalizeRoots(["c:", @"C:\Windows\", @"d:\data\", @"D:\Data\Sub", " "]);

        Assert.Equal(new[] { @"C:\", @"D:\data" }, roots);
    }

    private sealed record ScanResult(FsTree Tree, NameTable Names, SkippedCollector Skipped, Dictionary<string, int> Paths)
    {
        public IReadOnlyList<string> Methods { get; init; } = [];

        public static ScanResult Run(
            string[] roots,
            IgnoreMatcher? ignore = null,
            CancellationToken cancellationToken = default,
            FileSystemScanMethod method = FileSystemScanMethod.Automatic)
        {
            var names = new NameTable();
            var builder = new FsTreeBuilder(names);
            var skipped = new SkippedCollector();
            var scanner = new FileSystemScanner(builder, new Lock(), skipped);
            scanner.Scan(
                new FileSystemScanOptions { Roots = roots, Ignore = ignore ?? IgnoreMatcher.Empty, MaxDegreeOfParallelism = 4, Method = method },
                cancellationToken);

            var tree = builder.Build();
            var paths = Enumerable.Range(0, tree.Count)
                .ToDictionary(i => tree.GetFullPath(i, names), StringComparer.OrdinalIgnoreCase);
            return new ScanResult(tree, names, skipped, paths) { Methods = scanner.Methods };
        }

        /// <summary>Entries inside <paramref name="root"/>, keyed by path relative to it.</summary>
        public Dictionary<string, int> Below(string root)
        {
            var prefix = root.TrimEnd('\\') + "\\";
            return Paths
                .Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
