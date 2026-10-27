using System.Text;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Tests.Comparison;

/// <summary>Plan Step 31: hand-built snapshot pairs for every change kind and item type.</summary>
public class SnapshotComparerTests
{
    private const uint Dir = FsTree.DirectoryAttribute;
    private const uint Archive = 0x20;
    private const uint Hidden = 0x02;
    private const uint RegSz = 1;
    private const uint RegDword = 4;

    [Fact]
    public void IdenticalSnapshots_HaveNoDifferences()
    {
        var a = Build(Files, Keys, Services);
        var b = Build(Files, Keys, Services);

        var diff = SnapshotComparer.Compare(a, b);

        Assert.Equal(0, diff.FileSystem.Count);
        Assert.Equal(0, diff.Registry.Count);
        Assert.Equal(0, diff.Services.Count);
        Assert.Equal(0, diff.FileSystemCounts.Total);
    }

    [Fact]
    public void Files_EveryChangeKind()
    {
        var a = Build(Files);
        var b = Build(fs =>
        {
            var c = fs.AddRoot("C:", Dir, T(1));
            var windows = fs.Add(c, "Windows", Dir, 0, T(9));                 // folder time only: not a change itself
            var system32 = fs.Add(windows, "System32", Dir, 0, T(5));
            fs.Add(system32, "kernel32.dll", Archive, 812_000, T(3));
            fs.Add(system32, "user32.dll", Archive, 700_000, T(3));
            fs.Add(windows, "notepad.exe", Archive, 400_000, T(3));            // size changed
            fs.Add(windows, "regedit.exe", Archive, 100, T(8));                // time changed
            fs.Add(windows, "win.ini", Archive | Hidden, 50, T(3));            // attributes changed
            fs.Add(windows, "new.log", Archive, 10, T(9));                     // new file
            var fresh = fs.Add(c, "NewApp", Dir, 0, T(9));                     // new folder with contents
            fs.Add(fresh, "app.exe", Archive, 1, T(9));
            fs.Add(fresh, "app.dll", Archive, 1, T(9));
            fs.Add(c, "swap", Archive, 1, T(9));                               // was a folder, now a file
            fs.Add(c, "README.TXT", Archive, 5, T(1));                         // same file, name case changed
            fs.Add(c, "same", Dir, 0, T(1));
        });

        var diff = SnapshotComparer.Compare(a, b);
        var fs = diff.FileSystem;
        var byPath = Enumerable.Range(0, fs.Count)
            .GroupBy(n => diff.FullPath(fs, n), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase); // "swap" appears twice

        AssertNode(diff, fs, byPath[@"C:\Windows\notepad.exe"], ChangeKind.Modified, ChangedFields.Size);
        AssertNode(diff, fs, byPath[@"C:\Windows\regedit.exe"], ChangeKind.Modified, ChangedFields.LastWriteTime);
        AssertNode(diff, fs, byPath[@"C:\Windows\win.ini"], ChangeKind.Modified, ChangedFields.Attributes);
        AssertNode(diff, fs, byPath[@"C:\Windows\new.log"], ChangeKind.New, ChangedFields.None);
        AssertNode(diff, fs, byPath[@"C:\Windows\old.tmp"], ChangeKind.Deleted, ChangedFields.None);
        AssertNode(diff, fs, byPath[@"C:\NewApp"], ChangeKind.New, ChangedFields.None);
        AssertNode(diff, fs, byPath[@"C:\OldApp"], ChangeKind.Deleted, ChangedFields.None);
        AssertNode(diff, fs, byPath[@"C:\Windows"], ChangeKind.Unchanged, ChangedFields.None);

        // A new or deleted folder is one node for its whole content.
        Assert.Equal(0, fs.Descendants(byPath[@"C:\NewApp"]));
        Assert.Equal(3, fs.ChangedItems(byPath[@"C:\NewApp"]));

        // A file replaced by a folder of the same name is a deletion plus an addition.
        var swaps = Enumerable.Range(0, fs.Count).Where(n => diff.Name(fs, n) == "swap").ToList();
        Assert.Equal(2, swaps.Count);
        Assert.Contains(swaps, n => fs.Change(n) == ChangeKind.Deleted && fs.Kind(n) == DiffItemKind.Folder);
        Assert.Contains(swaps, n => fs.Change(n) == ChangeKind.New && fs.Kind(n) == DiffItemKind.File);

        // Not changes: a case-only rename, a folder whose own time changed, unchanged subtrees.
        Assert.DoesNotContain(@"C:\README.TXT", byPath.Keys);
        Assert.DoesNotContain(@"C:\same", byPath.Keys);
        Assert.DoesNotContain(@"C:\Windows\System32", byPath.Keys);

        // Counts: the parent of everything sums its changes.
        var drive = byPath["C:\\"];
        Assert.Equal(ChangeKind.Unchanged, fs.Change(drive));
        Assert.Equal(5 + 3 + 4 + 2 + 1, fs.ChangedItems(drive));
        Assert.Equal(new DiffCounts(New: 1 + 3 + 1, Deleted: 1 + 4 + 2, Modified: 3), diff.FileSystemCounts);
    }

    [Fact]
    public void Files_ChangeDeepInsideReportsOnlyItsPath()
    {
        var a = Build(Files);
        var b = Build(fs => Files(fs, deepChange: true));

        var diff = SnapshotComparer.Compare(a, b);
        var paths = Enumerable.Range(0, diff.FileSystem.Count).Select(n => diff.FullPath(diff.FileSystem, n)).ToArray();

        Assert.Equal(new[] { @"C:\", @"C:\Windows", @"C:\Windows\System32", @"C:\Windows\System32\kernel32.dll" }, paths);
        Assert.Equal(1, diff.FileSystem.ChangedItems(0));
    }

    [Fact]
    public void Registry_EveryChangeKind()
    {
        var a = Build(keys: Keys);
        var b = Build(keys: reg =>
        {
            var hklm = reg.AddRoot("HKLM", T(9));
            var software = reg.AddKey(hklm, "SOFTWARE", T(9));
            var vendor = reg.AddKey(software, "Vendor", T(9));
            reg.AddValue(vendor, string.Empty, RegSz, Sz("default changed"));   // default value: data
            reg.AddValue(vendor, "Count", RegSz, Sz("3"));                       // type changed (was DWORD 3)
            reg.AddValue(vendor, "Added", RegDword, BitConverter.GetBytes(1));    // new value
            reg.AddValue(vendor, "Same", RegSz, Sz("same"));
            var added = reg.AddKey(software, "NewVendor", T(9));                 // new key with a sub-key
            reg.AddKey(added, "Sub", T(9));
            reg.AddKey(software, "Classes", T(20));                              // key time only: not a change
        });

        var diff = SnapshotComparer.Compare(a, b);
        var reg = diff.Registry;
        var byPath = Enumerable.Range(0, reg.Count).ToDictionary(n => diff.FullPath(reg, n), StringComparer.OrdinalIgnoreCase);

        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\Vendor\"], ChangeKind.Modified, ChangedFields.ValueData);
        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\Vendor\Count"], ChangeKind.Modified, ChangedFields.ValueType | ChangedFields.ValueData);
        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\Vendor\Added"], ChangeKind.New, ChangedFields.None);
        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\Vendor\Removed"], ChangeKind.Deleted, ChangedFields.None);
        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\NewVendor"], ChangeKind.New, ChangedFields.None);
        AssertNode(diff, reg, byPath[@"HKLM\SOFTWARE\OldVendor"], ChangeKind.Deleted, ChangedFields.None);
        Assert.Equal(DiffItemKind.Value, reg.Kind(byPath[@"HKLM\SOFTWARE\Vendor\Added"]));
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Vendor\Same", byPath.Keys);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Classes", byPath.Keys);
        Assert.Equal(new DiffCounts(New: 1 + 2, Deleted: 1 + 1, Modified: 2), diff.RegistryCounts);
    }

    [Fact]
    public void Services_NewDeletedAndModifiedFields()
    {
        var a = Build(services: Services);
        var b = Build(services: s =>
        {
            s.Add("Spooler", "Print Spooler", 0x10, 1, 4, ServiceFlags.None);              // stopped and disabled
            s.Add("BITS", "Background Transfer", 0x20, 1, 2, ServiceFlags.DelayedAutoStart); // name, flags
            s.Add("NewSvc", "New", 0x10, 4, 2, ServiceFlags.None);
        });

        var diff = SnapshotComparer.Compare(a, b);
        var svc = diff.Services;
        var byName = Enumerable.Range(0, svc.Count).ToDictionary(n => diff.Name(svc, n));

        AssertNode(diff, svc, byName["Spooler"], ChangeKind.Modified, ChangedFields.State | ChangedFields.StartType);
        AssertNode(diff, svc, byName["BITS"], ChangeKind.Modified, ChangedFields.DisplayName | ChangedFields.ServiceFlags);
        AssertNode(diff, svc, byName["NewSvc"], ChangeKind.New, ChangedFields.None);
        AssertNode(diff, svc, byName["edgeupdate"], ChangeKind.Deleted, ChangedFields.None);
        Assert.Equal(new DiffCounts(1, 1, 2), diff.ServicesCounts);
    }

    [Fact]
    public void Swap_ExchangesSidesAndNewWithDeleted()
    {
        var a = Build(Files, Keys, Services);
        var b = Build(fs => Files(fs, deepChange: true, extraFile: true), Keys, s => s.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None));

        var diff = SnapshotComparer.Compare(a, b);
        var swapped = diff.Swap();

        Assert.Same(b, swapped.Left);
        Assert.Same(a, swapped.Right);
        Assert.Equal(diff.FileSystemCounts.New, swapped.FileSystemCounts.Deleted);
        Assert.Equal(diff.ServicesCounts.Deleted, swapped.ServicesCounts.New);
        for (var n = 0; n < diff.FileSystem.Count; n++)
        {
            Assert.Equal(diff.FileSystem.Left(n), swapped.FileSystem.Right(n));
            Assert.Equal(diff.FullPath(diff.FileSystem, n), swapped.FullPath(swapped.FileSystem, n));
            var expected = diff.FileSystem.Change(n) switch
            {
                ChangeKind.New => ChangeKind.Deleted,
                ChangeKind.Deleted => ChangeKind.New,
                var other => other,
            };
            Assert.Equal(expected, swapped.FileSystem.Change(n));
        }

        Assert.False(swapped.Swap().FileSystem.IsSwapped);
    }

    [Fact]
    public void IgnoreRules_HideMatchingChanges()
    {
        var a = Build(Files, Keys, Services);
        var b = Build(
            fs => Files(fs, deepChange: true, extraFile: true),
            Keys,
            s => s.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None));
        var ignore = GlobalIgnoreList.Build([@"FS:C:\Windows\System32", @"FS:*.log", "SVC:edge*"]).Matcher;

        var diff = SnapshotComparer.Compare(a, b, ignore);

        Assert.Equal(0, diff.FileSystem.Count);
        var services = Enumerable.Range(0, diff.Services.Count).Select(n => diff.Name(diff.Services, n)).ToArray();
        Assert.DoesNotContain("edgeupdate", services);
        Assert.Contains("BITS", services);
    }

    [Fact]
    public void EmptyAgainstFull_EverythingIsNew()
    {
        var a = Build();
        var b = Build(Files, Keys, Services);

        var diff = SnapshotComparer.Compare(a, b);

        Assert.All(diff.FileSystem.Roots(), n => Assert.Equal(ChangeKind.New, diff.FileSystem.Change(n)));
        Assert.Equal(b.FileSystem.Count, diff.FileSystemCounts.New);
        Assert.Equal(b.Services.Count, diff.ServicesCounts.New);
    }

    private static void AssertNode(DiffResult diff, DiffTree tree, int node, ChangeKind change, ChangedFields fields)
    {
        Assert.True(change == tree.Change(node), $"{diff.FullPath(tree, node)}: expected {change}, got {tree.Change(node)}");
        Assert.Equal(fields, tree.Fields(node));
        Assert.Equal(change != ChangeKind.New, tree.Left(node) >= 0);
        Assert.Equal(change != ChangeKind.Deleted, tree.Right(node) >= 0);
    }

    private static long T(int day) => new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    private static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static void Files(FsTreeBuilder fs) => Files(fs, deepChange: false);

    private static void Files(FsTreeBuilder fs, bool deepChange, bool extraFile = false)
    {
        var c = fs.AddRoot("C:", Dir, T(1));
        var windows = fs.Add(c, "Windows", Dir, 0, T(5));
        var system32 = fs.Add(windows, "System32", Dir, 0, T(5));
        fs.Add(system32, "kernel32.dll", Archive, deepChange ? 900_000 : 812_000, T(3));
        fs.Add(system32, "user32.dll", Archive, 700_000, T(3));
        if (extraFile)
        {
            fs.Add(system32, "trace.log", Archive, 1, T(4));
        }

        fs.Add(windows, "notepad.exe", Archive, 360_448, T(3));
        fs.Add(windows, "regedit.exe", Archive, 100, T(3));
        fs.Add(windows, "win.ini", Archive, 50, T(3));
        fs.Add(windows, "old.tmp", Archive, 1, T(3));
        var old = fs.Add(c, "OldApp", Dir, 0, T(2));
        fs.Add(old, "a.exe", Archive, 1, T(2));
        var oldSub = fs.Add(old, "data", Dir, 0, T(2));
        fs.Add(oldSub, "b.bin", Archive, 1, T(2));
        var swap = fs.Add(c, "swap", Dir, 0, T(2));
        fs.Add(swap, "x", Archive, 1, T(2));
        fs.Add(c, "Readme.txt", Archive, 5, T(1));
        fs.Add(c, "same", Dir, 0, T(2));
    }

    private static void Keys(RegTreeBuilder reg)
    {
        var hklm = reg.AddRoot("HKLM", T(1));
        var software = reg.AddKey(hklm, "SOFTWARE", T(1));
        var vendor = reg.AddKey(software, "Vendor", T(1));
        reg.AddValue(vendor, string.Empty, RegSz, Sz("default"));
        reg.AddValue(vendor, "Count", RegDword, BitConverter.GetBytes(3));
        reg.AddValue(vendor, "Removed", RegSz, Sz("gone soon"));
        reg.AddValue(vendor, "Same", RegSz, Sz("same"));
        var old = reg.AddKey(software, "OldVendor", T(1));
        reg.AddValue(old, "x", RegSz, Sz("x"));
        reg.AddKey(software, "Classes", T(1));
    }

    private static void Services(ServiceTableBuilder s)
    {
        s.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None);
        s.Add("BITS", "Background Intelligent Transfer Service", 0x20, 1, 2, ServiceFlags.DelayedAutoStart | ServiceFlags.TriggerStart);
        s.Add("edgeupdate", "Microsoft Edge Update Service", 0x10, 1, 3, ServiceFlags.TriggerStart);
    }

    private static Snapshot Build(
        Action<FsTreeBuilder>? files = null, Action<RegTreeBuilder>? keys = null, Action<ServiceTableBuilder>? services = null)
    {
        var names = new NameTable();
        var fs = new FsTreeBuilder(names);
        var reg = new RegTreeBuilder(names);
        var svc = new ServiceTableBuilder(names);
        files?.Invoke(fs);
        keys?.Invoke(reg);
        services?.Invoke(svc);
        var snapshot = new Snapshot(new SnapshotMeta(), names, fs.Build(), reg.Build(), svc.Build());
        names.Freeze();
        return snapshot;
    }
}
