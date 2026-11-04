using SysStateTracer.Core.CommandLine;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Tests.CommandLine;

/// <summary>Issue #8: /files:, /registry:, /services: and /output: for /report and /export.</summary>
public class CliFilterTests
{
    [Fact]
    public void Parse_FiltersAndOutput()
    {
        // cmd passes /registry:"HKCU","HKLM\Software" as /registry:HKCU,HKLM\Software.
        var export = CliParser.Parse(["/export", "1", "2", @"/registry:HKCU,HKLM\Software", @"/files:C:\1", @"-files:""C:\2, with comma""", @"--output:C:\test"]).Command!;
        Assert.Equal(["1", "2"], export.Names);
        Assert.Equal(["HKCU", @"HKLM\Software"], export.Registry);
        Assert.Equal([@"C:\1", @"C:\2", "with comma"], export.Files);
        Assert.Equal(@"C:\test", export.OutputPath);

        var compare = CliParser.Parse(["/report", "/services:Spooler,BITS", @"/OUTPUT:C:\r\x.html"]).Command!;
        Assert.Empty(compare.Names);
        Assert.Equal(["Spooler", "BITS"], compare.Services);
        Assert.Equal(@"C:\r\x.html", compare.ReportPath);
        Assert.True(compare.HasFilters);

        // A repeated switch adds to the list, so a path with a comma can be given on its own.
        Assert.Equal([@"C:\A", @"D:\B"], CliParser.Parse(["/export", @"/files:C:\A", @"/files:D:\B"]).Command!.Files);

        // Environment variables: System\name or User\name, for reports.
        var env = CliParser.Parse(["/report", @"/environment:System\Path,User\*"]).Command!;
        Assert.Equal([@"System\Path", @"User\*"], env.Environment);
        Assert.True(env.HasFilters);
    }

    [Theory]
    [InlineData("/export", "/file:C:\\1")]
    [InlineData("/export", "/files:")]
    [InlineData("/export", "/files:C:\\1,,C:\\2")]
    [InlineData("/export", "/services:Spooler")]
    [InlineData("/export", "/environment:System\\Path")]
    [InlineData("/export", "/output:C:\\r.html")]
    [InlineData("/export", "/output:C:\\a", "/output:C:\\b")]
    [InlineData("/report", "/output:C:\\r.txt")]
    [InlineData("/report", "/output:C:\\r.html", "C:\\s.html")]
    [InlineData("/new", "/files:C:\\1")]
    public void Parse_WrongFiltersOrOutput(params string[] args)
    {
        var result = CliParser.Parse(args);
        Assert.Null(result.Command);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Export_RegistryAndFiles_IntoOutputFolder_WithScriptAndRegBesideFiles()
    {
        using var env = new FilterEnvironment();
        env.AddSnapshots();
        var output = Path.Combine(env.Root, "out", "test");

        Assert.Equal(0, env.Run("/export", "before", "after", @"/registry:HKLM\SOFTWARE\App,HKCU\Software\App", $"/files:{env.Data}\\1,{env.Data}\\2", $"/output:{output}"));

        // C:\test is created, with the .reg file and the restore script in it and the copies under Files.
        Assert.Contains($"Created the output folder {output}", env.Text, StringComparison.Ordinal);
        var reg = Assert.Single(Directory.GetFiles(output, "*.reg"));
        var regText = File.ReadAllText(reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\App]", regText, StringComparison.Ordinal);
        Assert.Contains(@"[HKEY_USERS\S-1-5-21-1000\Software\App]", regText, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", regText, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "Restore-Files.ps1")));
        Assert.Contains("Join-Path $PSScriptRoot", File.ReadAllText(Path.Combine(output, "Restore-Files.ps1")), StringComparison.Ordinal);
        var copied = Directory.GetFiles(Path.Combine(output, "Files"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["a.txt", "new.txt"], copied); // not Other\x.txt
        Assert.False(File.Exists(Path.Combine(output, "Files", "Restore-Files.ps1")));

        // What each filter matched is printed.
        Assert.Matches(@"Registry:\s+HKLM\\SOFTWARE\\App: 1 changes", env.Text);
        Assert.Matches(@"HKCU\\Software\\App: 1 changes", env.Text);
        Assert.Contains(@"(matched as HKU\*\Software\App)", env.Text, StringComparison.Ordinal);
        Assert.Matches(@"\\1: 1 changes", env.Text);
    }

    [Fact]
    public void Export_RegistryOnly_ToARegFile_CopiesNoFiles()
    {
        using var env = new FilterEnvironment();
        env.AddSnapshots();
        var reg = Path.Combine(env.Root, "exports", "app.reg");

        Assert.Equal(0, env.Run("/export", "before", "after", @"/registry:HKLM", $"/output:{reg}"));

        var text = File.ReadAllText(reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\App]", text, StringComparison.Ordinal);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SYSTEM\Other]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HKEY_USERS", text, StringComparison.Ordinal);
        Assert.Contains("Files: not copied (no /files: filter).", env.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(env.Root, "exports", "Files")));
    }

    [Fact]
    public void CompareSnap_Filters_KeepOnlyWhatTheyName_AndWarnWhenNothingMatches()
    {
        using var env = new FilterEnvironment();
        env.AddSnapshots();
        var report = Path.Combine(env.Root, "reports", "r.html");

        Assert.Equal(0, env.Run("/report", "before", "after", "/services:Spooler", @"/registry:HKLM\SOFTWARE\Missing", $"/output:{report}"));

        var html = File.ReadAllText(report);
        Assert.Contains("Spooler", html, StringComparison.Ordinal);
        Assert.DoesNotContain("BITS", html, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt", html, StringComparison.Ordinal);
        Assert.Contains(@"Warning: nothing new, modified or deleted matches HKLM\SOFTWARE\Missing", env.Text, StringComparison.Ordinal);
        Assert.Matches(@"After the filters:[\s\S]*Services:\s+0 new\s+1 modified\s+0 deleted", env.Text);
    }

    [Fact]
    public void Output_ThatCannotBeCreated_Exit4WithTheReason_BeforeComparing()
    {
        using var env = new FilterEnvironment();
        env.AddSnapshots();
        var file = Path.Combine(env.Root, "a file");
        File.WriteAllText(file, "x");

        Assert.Equal(4, env.Run("/export", "before", "after", $"/output:{Path.Combine(file, "sub")}"));
        Assert.Contains("could not be created", env.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Comparing", env.Text, StringComparison.Ordinal);

        Assert.Equal(4, env.Run("/export", "before", "after", $"/output:{file}"));
        Assert.Contains("is a file", env.Text, StringComparison.Ordinal);

        Assert.Equal(1, env.Run("/export", "before", "after", "/registry:HKXX\\Software"));
        Assert.Contains("must start with HKLM", env.Text, StringComparison.Ordinal);
    }

    /// <summary>Snapshots of real files in a temporary folder, a few registry keys and two services.</summary>
    private sealed class FilterEnvironment : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly StringWriter _output = new();

        public FilterEnvironment()
        {
            DataFolder = Directory.CreateDirectory(Path.Combine(_root.Path, "app")).FullName;
            SnapshotFolder = Directory.CreateDirectory(Path.Combine(_root.Path, "snapshots")).FullName;
            Data = Directory.CreateDirectory(Path.Combine(_root.Path, "data")).FullName;
        }

        public string Root => _root.Path;

        public string DataFolder { get; }

        public string SnapshotFolder { get; }

        /// <summary>The folder holding 1, 2 and Other, on disk and in the snapshots.</summary>
        public string Data { get; }

        public string Text => _output.ToString();

        public int Run(params string[] args)
        {
            _output.GetStringBuilder().Clear();
            return CliRunner.Run(args, new CliEnvironment
            {
                Paths = AppPaths.Resolve(DataFolder, Path.Combine(_root.Path, "fallback"), _ => true),
                Settings = new AppSettings { SnapshotFolder = SnapshotFolder, IgnoreListSource = IgnoreListSource.Local },
                Output = _output,
                LoadIgnoreList = _ => new IgnoreListState(GlobalIgnoreList.Build([]), IgnoreListOrigin.Local, "Test list."),
            });
        }

        /// <summary>
        /// "after" changes 1\a.txt, adds 2\new.txt and changes Other\x.txt; changes HKLM\SOFTWARE\App,
        /// HKLM\SYSTEM\Other and the user's Software\App; and changes both services.
        /// </summary>
        public void AddSnapshots()
        {
            foreach (var folder in new[] { "1", "2", "Other" })
            {
                Directory.CreateDirectory(Path.Combine(Data, folder));
            }

            File.WriteAllText(Path.Combine(Data, "1", "a.txt"), "changed");
            File.WriteAllText(Path.Combine(Data, "2", "new.txt"), "new");
            File.WriteAllText(Path.Combine(Data, "Other", "x.txt"), "changed");
            Add("before", day: 1, after: false);
            Add("after", day: 2, after: true);
        }

        private void Add(string name, int day, bool after)
        {
            var names = new NameTable();
            var fs = new FsTreeBuilder(names);
            var parts = Data.Split('\\');
            var node = fs.AddRoot(parts[0], FsTree.DirectoryAttribute, SampleSnapshots.Time(1));
            foreach (var part in parts[1..])
            {
                node = fs.Add(node, part, FsTree.DirectoryAttribute, 0, SampleSnapshots.Time(1));
            }

            var one = fs.Add(node, "1", FsTree.DirectoryAttribute, 0, SampleSnapshots.Time(1));
            fs.Add(one, "a.txt", SampleSnapshots.Archive, after ? 7 : 3, SampleSnapshots.Time(day));
            var two = fs.Add(node, "2", FsTree.DirectoryAttribute, 0, SampleSnapshots.Time(1));
            if (after)
            {
                fs.Add(two, "new.txt", SampleSnapshots.Archive, 3, SampleSnapshots.Time(day));
            }

            var other = fs.Add(node, "Other", FsTree.DirectoryAttribute, 0, SampleSnapshots.Time(1));
            fs.Add(other, "x.txt", SampleSnapshots.Archive, after ? 7 : 3, SampleSnapshots.Time(day));

            var reg = new RegTreeBuilder(names);
            var hklm = reg.AddRoot("HKLM", SampleSnapshots.Time(1));
            var app = reg.AddKey(reg.AddKey(hklm, "SOFTWARE", SampleSnapshots.Time(1)), "App", SampleSnapshots.Time(day));
            reg.AddValue(app, "Version", 1, SampleSnapshots.Sz(after ? "2" : "1"));
            var otherKey = reg.AddKey(reg.AddKey(hklm, "SYSTEM", SampleSnapshots.Time(1)), "Other", SampleSnapshots.Time(day));
            reg.AddValue(otherKey, "Setting", 1, SampleSnapshots.Sz(after ? "on" : "off"));
            var hku = reg.AddRoot("HKU", SampleSnapshots.Time(1));
            var userApp = reg.AddKey(reg.AddKey(reg.AddKey(hku, "S-1-5-21-1000", SampleSnapshots.Time(1)), "Software", SampleSnapshots.Time(1)), "App", SampleSnapshots.Time(day));
            if (after)
            {
                reg.AddValue(userApp, "Theme", 1, SampleSnapshots.Sz("dark"));
            }

            var services = new ServiceTableBuilder(names);
            services.Add("Spooler", "Print Spooler", 0x10, after ? 1u : 4u, 2, ServiceFlags.None);
            services.Add("BITS", "Background Intelligent Transfer Service", 0x20, 4, after ? 3u : 2u, ServiceFlags.None);

            var meta = new SnapshotMeta { Name = name, StartedAt = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero), ComputerName = "PC" };
            var snapshot = new Snapshot(meta, names, fs.Build(), reg.Build(), services.Build(), []);
            names.Freeze();
            SnapshotWriter.Write(Path.Combine(SnapshotFolder, name + ".stsnap"), snapshot);
        }

        public void Dispose()
        {
            _output.Dispose();
            _root.Dispose();
        }
    }
}
