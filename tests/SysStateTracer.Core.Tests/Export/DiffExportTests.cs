using System.Text;
using System.Text.Json;
using SysStateTracer.Core.Actions;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Tests.Export;

/// <summary>Exports and helpers built on a comparison (plan Steps 33–38, F3).</summary>
public class DiffExportTests
{
    private const uint Dir = FsTree.DirectoryAttribute;
    private const uint Archive = 0x20;

    [Fact]
    public void Registry_ExportsNewAndModifiedFromTheNewerSnapshot()
    {
        var diff = Compare();
        var (file, notStored) = DiffExport.Registry(diff, diff.Registry.Roots());
        var text = file.ToString();

        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Vendor]\r\n\"Added\"=dword:00000001\r\n\"Changed\"=\"new\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\NewApp]\r\n\"v\"=\"1\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\NewApp\\Sub]\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OldApp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Same", text, StringComparison.Ordinal);
        Assert.Equal(@"HKLM\SOFTWARE\NewApp\Huge", Assert.Single(notStored));
    }

    [Fact]
    public void Registry_LeavesOutNonImportantItems()
    {
        var diff = Compare();
        var marks = new ImportanceMarks();
        var newApp = Find(diff, diff.Registry, @"HKLM\SOFTWARE\NewApp");
        marks.Toggle([new DiffItem(DiffArea.Registry, newApp)]);

        var text = DiffExport.Registry(diff, diff.Registry.Roots(), marks).File.ToString();

        Assert.DoesNotContain("NewApp", text, StringComparison.Ordinal);
        Assert.Contains("Vendor", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_WholeDriveFromItsTopNode_WithoutNonImportant()
    {
        var diff = Compare();
        var marks = new ImportanceMarks();
        marks.Toggle([new DiffItem(DiffArea.FileSystem, Find(diff, diff.FileSystem, @"C:\Temp"))]);

        var files = DiffExport.Files(diff, diff.FileSystem.Roots(), marks, systemDrive: 'C');

        Assert.Equal(
            new[]
            {
                (@"C:\App\new.exe", @"App\new.exe"),
                (@"C:\App\changed.dll", @"App\changed.dll"),
                (@"C:\NewFolder\a.txt", @"NewFolder\a.txt"),
                (@"C:\NewFolder\sub\b.txt", @"NewFolder\sub\b.txt"),
                (@"D:\Data\report.xlsx", @"_D\Data\report.xlsx"),
            }.Order(),
            files.Select(f => (f.Source, f.RelativeTarget)).Order());
        Assert.Equal(2_000, files.Single(f => f.Source.EndsWith("changed.dll", StringComparison.Ordinal)).Size);
    }

    [Fact]
    public void Files_SelectionOfOneFolderAndAFileInsideItIsNotDoubled()
    {
        var diff = Compare();
        var app = Find(diff, diff.FileSystem, @"C:\App");
        var file = Find(diff, diff.FileSystem, @"C:\App\new.exe");

        var files = DiffExport.Files(diff, [app, file], systemDrive: 'C');

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Swap_DoesNotChangeWhatIsExported()
    {
        var diff = Compare();
        var swapped = diff.Swap();

        Assert.Equal(
            DiffExport.Registry(diff, diff.Registry.Roots()).File.ToString(),
            DiffExport.Registry(swapped, swapped.Registry.Roots()).File.ToString());
        Assert.Equal(
            DiffExport.Files(diff, diff.FileSystem.Roots(), systemDrive: 'C').Select(f => f.Source),
            DiffExport.Files(swapped, swapped.FileSystem.Roots(), systemDrive: 'C').Select(f => f.Source));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Test\x.txt", 'C', @"ProgramData\Test\x.txt")]
    [InlineData(@"c:\ProgramData\Test\x.txt", 'C', @"ProgramData\Test\x.txt")]
    [InlineData(@"D:\Data\X", 'C', @"_D\Data\X")]
    [InlineData(@"C:\Windows\a", 'D', @"_C\Windows\a")]
    public void TargetPath_KeepsStructureAndPrefixesOtherDrives(string source, char system, string expected)
    {
        Assert.Equal(expected, DiffExport.TargetPath(source, system));
    }

    [Fact]
    public void Revert_PlansFromTheOlderSnapshot()
    {
        var diff = Compare();
        var vendor = Find(diff, diff.Registry, @"HKLM\SOFTWARE\Vendor");
        var newApp = Find(diff, diff.Registry, @"HKLM\SOFTWARE\NewApp");
        var oldApp = Find(diff, diff.Registry, @"HKLM\SOFTWARE\OldApp");

        var items = RegistryReverter.Plan(diff.Swap(), [vendor, newApp, oldApp]).ToDictionary(i => i.DisplayPath);

        Assert.Equal(RevertAction.Restore, items[@"HKLM\SOFTWARE\Vendor\Changed"].Action);
        Assert.Equal(Sz("old"), items[@"HKLM\SOFTWARE\Vendor\Changed"].Data);
        Assert.Equal(RevertAction.Restore, items[@"HKLM\SOFTWARE\Vendor\Removed"].Action);
        Assert.Equal(RevertAction.Delete, items[@"HKLM\SOFTWARE\Vendor\Added"].Action);
        Assert.Equal(RevertAction.Delete, items[@"HKLM\SOFTWARE\NewApp\v"].Action);
        Assert.Equal(RevertAction.Restore, items[@"HKLM\SOFTWARE\OldApp\x"].Action);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Vendor\Same", items.Keys);
        Assert.All(items.Values, i => Assert.Null(i.Problem));
    }

    [Fact]
    public void Marks_ToggleTogetherAndCoverChildren()
    {
        var diff = Compare();
        var app = new DiffItem(DiffArea.FileSystem, Find(diff, diff.FileSystem, @"C:\App"));
        var file = new DiffItem(DiffArea.FileSystem, Find(diff, diff.FileSystem, @"C:\App\new.exe"));
        var marks = new ImportanceMarks();

        marks.Toggle([app]);
        Assert.True(marks.IsMarkedOrInside(diff, file));
        Assert.False(marks.IsMarked(file));

        marks.Toggle([app, file]);   // one unmarked → mark both
        Assert.Equal(2, marks.Count);
        marks.Toggle([app, file]);   // all marked → unmark both
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Columns_AndIgnoreRules()
    {
        using var culture = new CultureScope("en-US");
        var diff = Compare();
        var changed = new DiffItem(DiffArea.FileSystem, Find(diff, diff.FileSystem, @"C:\App\changed.dll"));
        var value = new DiffItem(DiffArea.Registry, Find(diff, diff.Registry, @"HKLM\SOFTWARE\Vendor\Added"));
        var service = new DiffItem(DiffArea.Services, 0);

        Assert.Contains("1,000 B", DiffItems.Column(diff, changed, right: false), StringComparison.Ordinal);
        Assert.Contains("2 KB", DiffItems.Column(diff, changed, right: true), StringComparison.Ordinal);
        Assert.Equal(string.Empty, DiffItems.Column(diff, value, right: false));
        Assert.Equal("REG_DWORD · 0x00000001 (1)", DiffItems.Column(diff, value, right: true));
        Assert.Equal("Running · Automatic (Delayed Start)", DiffItems.Column(diff, service, right: true));

        Assert.Equal(@"FS:C:\App\changed.dll", DiffItems.IgnoreRule(diff, changed));
        Assert.Equal(@"REG:HKLM\SOFTWARE\Vendor\Added", DiffItems.IgnoreRule(diff, value));
        Assert.Equal("SVC:Svc", DiffItems.IgnoreRule(diff, service));
    }

    [Fact]
    public void HtmlReport_IsSelfContainedWithSummaryAndData()
    {
        var diff = Compare();
        using var dir = new TempDirectory();
        var path = dir.File("results.html");

        HtmlReport.Write(diff, path);
        var html = File.ReadAllText(path);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"http", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http", html, StringComparison.Ordinal);
        var start = html.IndexOf("<script type=\"application/json\" id=\"data\">", StringComparison.Ordinal);
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        var json = html[(start + "<script type=\"application/json\" id=\"data\">".Length)..end];
        using var data = JsonDocument.Parse(json);
        Assert.Equal(3, data.RootElement.GetArrayLength());
        Assert.Equal("File system", data.RootElement[0][0].GetString());
        // What new and deleted folders and keys hold is listed too, so they open to any depth (issue #3):
        // NewFolder (sub, sub\b.txt, a.txt) and D:\Data (report.xlsx); NewApp (Sub, v, Huge) and OldApp (x).
        var files = data.RootElement[0][1].EnumerateArray().ToList();
        Assert.Equal(diff.FileSystem.Count + 4, files.Count);
        var newFolder = files.FindIndex(r => r[0].GetString() == "NewFolder");
        Assert.Equal(3, files[newFolder][6].GetInt32());
        Assert.Equal(new[] { "sub", "b.txt", "a.txt" }, files.Skip(newFolder + 1).Take(3).Select(r => r[0].GetString()!));
        Assert.Equal(1, files[newFolder + 1][6].GetInt32());
        Assert.Equal((int)ChangeKind.New, files[newFolder + 2][2].GetInt32());
        Assert.Equal(string.Empty, files[newFolder + 2][3].GetString());
        var keys = data.RootElement[1][1].EnumerateArray().ToList();
        Assert.Equal(diff.Registry.Count + 4, keys.Count);
        var newApp = keys.FindIndex(r => r[0].GetString() == "NewApp");
        Assert.Equal(new[] { "Sub", "Huge", "v" }, keys.Skip(newApp + 1).Take(3).Select(r => r[0].GetString()!));
        Assert.Contains("<td class=\"n\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_Commands()
    {
        var command = ShellLauncher.ExplorerCommand(@"C:\Gone\deeper\file.txt", p => p == @"C:\Gone");
        Assert.Equal(new LaunchCommand("explorer.exe", "\"C:\\Gone\""), command);
        Assert.Equal("/select,\"C:\\Here.txt\"", ShellLauncher.ExplorerCommand(@"C:\Here.txt", _ => true).Arguments);

        Assert.Equal(@"Computer\HKEY_LOCAL_MACHINE\SOFTWARE\X", ShellLauncher.RegeditLastKey(@"HKLM\SOFTWARE\X", "S-1-5-21-9"));
        Assert.Equal(@"Computer\HKEY_CURRENT_USER\Software\X", ShellLauncher.RegeditLastKey(@"HKU\S-1-5-21-9\Software\X", "S-1-5-21-9"));
        Assert.Equal(@"Computer\HKEY_USERS\S-1-5-21-8\Software", ShellLauncher.RegeditLastKey(@"HKU\S-1-5-21-8\Software", "S-1-5-21-9"));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void ItemFormat_Size(long bytes, string expected)
    {
        using var culture = new CultureScope("en-US");
        Assert.Equal(expected, ItemFormat.Size(bytes));
    }

    [Fact]
    public void ItemFormat_ValuesAndServices()
    {
        Assert.Equal("a ¶ b", ItemFormat.ValueData(ItemFormat.RegMultiSz, Encoding.Unicode.GetBytes("a\0b\0\0")));
        Assert.Equal("0x0000000000000010 (16)", ItemFormat.ValueData(ItemFormat.RegQword, BitConverter.GetBytes(16L)));
        Assert.Equal("01 02", ItemFormat.ValueData(ItemFormat.RegBinary, [1, 2]));
        Assert.EndsWith("(100 bytes)", ItemFormat.ValueData(ItemFormat.RegBinary, new byte[100]), StringComparison.Ordinal);
        Assert.Equal("Manual (Trigger Start)", ItemFormat.StartType(3, ServiceFlags.TriggerStart));
        Assert.Equal("Automatic (Delayed Start, Trigger Start)", ItemFormat.StartType(2, ServiceFlags.DelayedAutoStart | ServiceFlags.TriggerStart));
        Assert.Equal("Disabled", ItemFormat.StartType(4, ServiceFlags.None));
        Assert.Equal("RHA", ItemFormat.Attributes(0x23));
    }

    private static int Find(DiffResult diff, DiffTree tree, string path) =>
        Enumerable.Range(0, tree.Count).Single(n => diff.FullPath(tree, n).Equals(path, StringComparison.OrdinalIgnoreCase));

    private static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static long T(int day) => new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>Older and newer snapshots with every kind of change.</summary>
    private static DiffResult Compare()
    {
        var older = Build(T(1), fs =>
        {
            var c = fs.AddRoot("C:", Dir, T(1));
            var app = fs.Add(c, "App", Dir, 0, T(1));
            fs.Add(app, "changed.dll", Archive, 1_000, T(1));
            fs.Add(app, "removed.ini", Archive, 5, T(1));
            fs.Add(app, "same.txt", Archive, 5, T(1));
            var temp = fs.Add(c, "Temp", Dir, 0, T(1));
            fs.Add(temp, "t1.tmp", Archive, 5, T(1));
            fs.AddRoot("D:", Dir, T(1));
        }, reg =>
        {
            var hklm = reg.AddRoot("HKLM", T(1));
            var software = reg.AddKey(hklm, "SOFTWARE", T(1));
            var vendor = reg.AddKey(software, "Vendor", T(1));
            reg.AddValue(vendor, "Changed", 1, Sz("old"));
            reg.AddValue(vendor, "Removed", 1, Sz("bye"));
            reg.AddValue(vendor, "Same", 1, Sz("same"));
            var old = reg.AddKey(software, "OldApp", T(1));
            reg.AddValue(old, "x", 1, Sz("x"));
        }, svc => svc.Add("Svc", "Service", 0x10, 1, 2, ServiceFlags.DelayedAutoStart));

        var newer = Build(T(2), fs =>
        {
            var c = fs.AddRoot("C:", Dir, T(2));
            var app = fs.Add(c, "App", Dir, 0, T(2));
            fs.Add(app, "changed.dll", Archive, 2_000, T(2));
            fs.Add(app, "new.exe", Archive, 7, T(2));
            fs.Add(app, "same.txt", Archive, 5, T(1));
            var temp = fs.Add(c, "Temp", Dir, 0, T(2));
            fs.Add(temp, "t2.tmp", Archive, 5, T(2));
            var folder = fs.Add(c, "NewFolder", Dir, 0, T(2));
            fs.Add(folder, "a.txt", Archive, 1, T(2));
            var sub = fs.Add(folder, "sub", Dir, 0, T(2));
            fs.Add(sub, "b.txt", Archive, 1, T(2));
            var d = fs.AddRoot("D:", Dir, T(2));
            var data = fs.Add(d, "Data", Dir, 0, T(2));
            fs.Add(data, "report.xlsx", Archive, 3, T(2));
        }, reg =>
        {
            var hklm = reg.AddRoot("HKLM", T(2));
            var software = reg.AddKey(hklm, "SOFTWARE", T(2));
            var vendor = reg.AddKey(software, "Vendor", T(2));
            reg.AddValue(vendor, "Changed", 1, Sz("new"));
            reg.AddValue(vendor, "Added", 4, BitConverter.GetBytes(1));
            reg.AddValue(vendor, "Same", 1, Sz("same"));
            var app = reg.AddKey(software, "NewApp", T(2));
            reg.AddValue(app, "v", 1, Sz("1"));
            reg.AddValue(app, "Huge", 3, new byte[RegTreeBuilder.MaxStoredValueSize + 1]);
            reg.AddKey(app, "Sub", T(2));
        }, svc => svc.Add("Svc", "Service", 0x10, 4, 2, ServiceFlags.DelayedAutoStart));

        return SnapshotComparer.Compare(older, newer);
    }

    private static Snapshot Build(long started, Action<FsTreeBuilder> files, Action<RegTreeBuilder> keys, Action<ServiceTableBuilder> services)
    {
        var names = new NameTable();
        var fs = new FsTreeBuilder(names);
        var reg = new RegTreeBuilder(names);
        var svc = new ServiceTableBuilder(names);
        files(fs);
        keys(reg);
        services(svc);
        var meta = new SnapshotMeta
        {
            Name = started == T(1) ? "older" : "newer",
            StartedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(started)),
            ComputerName = "PC",
        };
        var snapshot = new Snapshot(meta, names, fs.Build(), reg.Build(), svc.Build());
        names.Freeze();
        return snapshot;
    }

    /// <summary>Sets the current culture for one test.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _before = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _before;
    }
}
