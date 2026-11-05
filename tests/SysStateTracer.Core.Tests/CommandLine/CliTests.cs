using SysStateTracer.Core.CommandLine;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Tests.CommandLine;

/// <summary>Plan Steps 39–40: parsing, /?, and the switches that need no scanning.</summary>
public class CliTests
{
    [Theory]
    [InlineData("/?", CliCommandKind.Help)]
    [InlineData("-?", CliCommandKind.Help)]
    [InlineData("--help", CliCommandKind.Help)]
    [InlineData("/new", CliCommandKind.NewSnap)]
    [InlineData("/NEW", CliCommandKind.NewSnap)]
    [InlineData("/del", CliCommandKind.DeleteSnap)]
    [InlineData("/report", CliCommandKind.CompareSnap)]
    [InlineData("/export", CliCommandKind.Export)]

    // The names before 1.2.0 still work.
    [InlineData("/NEW-SNAP", CliCommandKind.NewSnap)]
    [InlineData("-new-snap", CliCommandKind.NewSnap)]
    [InlineData("/delete-snap", CliCommandKind.DeleteSnap)]
    [InlineData("/compare-snap", CliCommandKind.CompareSnap)]
    public void Parse_Switches(string arg, CliCommandKind kind)
    {
        var result = CliParser.Parse([arg]);
        Assert.Null(result.Error);
        Assert.Equal(kind, result.Command!.Kind);
        Assert.Empty(result.Command.Names);
    }

    [Fact]
    public void Parse_PermissionsWithNew()
    {
        var command = CliParser.Parse(["/new", "/permissions", "Before install"]).Command!;
        Assert.True(command.Permissions);
        Assert.Equal(["Before install"], command.Names);

        Assert.False(CliParser.Parse(["/new", "Before install"]).Command!.Permissions);
        Assert.NotNull(CliParser.Parse(["/report", "/permissions"]).Error);
    }

    [Fact]
    public void Parse_Arguments()
    {
        Assert.Equal(["Before install"], CliParser.Parse(["/new", "Before install"]).Command!.Names);
        Assert.Equal(["a", "b"], CliParser.Parse(["/rename", "a", "b"]).Command!.Names);
        Assert.Equal(CliCommandKind.RenameSnap, CliParser.Parse(["-rename", "a", "b"]).Command!.Kind);
        Assert.Equal(CliCommandKind.RenameSnap, CliParser.Parse(["/rename-snap", "a", "b"]).Command!.Kind); // the name before 1.2.0

        var compare = CliParser.Parse(["/report", "before", @"C:\Reports\Office changes.HTML", "after"]).Command!;
        Assert.Equal(["before", "after"], compare.Names);
        Assert.Equal(@"C:\Reports\Office changes.HTML", compare.ReportPath);

        var reportOnly = CliParser.Parse(["/report", "report.html"]).Command!;
        Assert.Empty(reportOnly.Names);
        Assert.Equal("report.html", reportOnly.ReportPath);
    }

    [Theory]
    [InlineData]
    [InlineData("before")]
    [InlineData("/unknown")]
    [InlineData("/new", "a", "b")]
    [InlineData("/del", "a", "b")]
    [InlineData("/rename", "only-one")]
    [InlineData("/rename", "a", "b", "c")]
    [InlineData("/report", "only-one")]
    [InlineData("/report", "a", "b", "one.html", "two.html")]
    [InlineData("/export", "only-one")]
    [InlineData("/export", "a", "b", "c")]
    [InlineData("/new", "/del")]
    [InlineData("/new", " ")]
    public void Parse_WrongArguments(params string[] args)
    {
        var result = CliParser.Parse(args);
        Assert.Null(result.Command);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Help_ListsSwitchesExamplesFoldersAndGitHub()
    {
        using var env = new TestEnvironment();

        var code = env.Run("/?");

        Assert.Equal(0, code);
        var text = env.Text;
        foreach (var name in new[] { "/new", "/del", "/rename", "/report", "/export", "Exit codes:", "Examples:" })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }

        Assert.Contains(AppInfo.GitHubUrl, text, StringComparison.Ordinal);
        Assert.Contains(env.SnapshotFolder, text, StringComparison.Ordinal);
        Assert.Contains("/new \"Before Office 2024 install\"", text, StringComparison.Ordinal);
        Assert.Contains("\"C:\\Reports\\Office 2024 install changes.html\"", text, StringComparison.Ordinal);
        Assert.All(
            text.Split(Environment.NewLine).Where(l => !l.Contains(env.SnapshotFolder, StringComparison.Ordinal) && !l.Contains(env.DataFolder, StringComparison.Ordinal)),
            line => Assert.True(line.Length <= 140, line));
    }

    [Fact]
    public void WrongArguments_Exit1WithHint()
    {
        using var env = new TestEnvironment();

        Assert.Equal(1, env.Run("/rename", "only-one"));
        Assert.Contains("/?", env.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteSnap_ByNameAndNewest()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("first", day: 1);
        env.AddSnapshot("second", day: 2);
        env.AddSnapshot("third", day: 3);

        Assert.Equal(0, env.Run("/del", "first.stsnap"));
        Assert.False(File.Exists(env.PathOf("first")));
        Assert.Contains("\"first\"", env.Text, StringComparison.Ordinal);

        // Newest by the time stored in the snapshot, not the file time.
        File.SetLastWriteTimeUtc(env.PathOf("second"), DateTime.UtcNow.AddDays(1));
        Assert.Equal(0, env.Run("/del"));
        Assert.False(File.Exists(env.PathOf("third")));
        Assert.True(File.Exists(env.PathOf("second")));

        Assert.Equal(2, env.Run("/del", "never taken"));
        Assert.Equal(0, env.Run("/del"));
        Assert.Equal(2, env.Run("/del"));
    }

    [Fact]
    public void RenameSnap()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("old name", day: 1);
        env.AddSnapshot("taken", day: 2);

        Assert.Equal(0, env.Run("/rename", "old name", "Office 2024 baseline"));
        Assert.True(File.Exists(env.PathOf("Office 2024 baseline")));
        Assert.False(File.Exists(env.PathOf("old name")));

        Assert.Equal(0, env.Run("/rename", "Office 2024 baseline", "OFFICE 2024 baseline"));
        Assert.Equal(2, env.Run("/rename", "missing", "x"));
        Assert.Equal(1, env.Run("/rename", "taken", "OFFICE 2024 baseline"));
        Assert.Equal(1, env.Run("/rename", "taken", "bad:name"));
        Assert.True(File.Exists(env.PathOf("taken")));
    }

    [Fact]
    public void NamesWithExtensionAndPaths()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("first", day: 1);
        env.AddSnapshot("second", day: 2);
        var elsewhere = Path.Combine(env.DataFolder, "elsewhere");
        var moved = Path.Combine(elsewhere, "moved.stsnap");

        // Renaming to a path moves the snapshot there.
        Assert.Equal(0, env.Run("/rename", "first.stsnap", moved));
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(env.PathOf("first")));

        // One snapshot by path, the other by name with the extension.
        Assert.Equal(0, env.Run("/report", moved, "second.stsnap", "by path.html"));
        Assert.Contains("moved vs second", File.ReadAllText(Path.Combine(env.DataFolder, "by path.html")), StringComparison.Ordinal);

        Assert.Equal(2, env.Run("/del", Path.Combine(elsewhere, "nothing.stsnap")));
        Assert.Contains("No snapshot file", env.Text, StringComparison.Ordinal);
        Assert.Equal(0, env.Run("/del", moved));
        Assert.False(File.Exists(moved));
        Assert.Equal(1, env.Run("/rename", "second", Path.Combine(elsewhere, "bad|name.stsnap")));
    }

    [Fact]
    public void CompareSnap_WritesReport_NewestTwoByDefault()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("oldest", day: 1, notepadSize: 1);
        env.AddSnapshot("before", day: 2, notepadSize: 2);
        env.AddSnapshot("after", day: 3, notepadSize: 3, extraFile: true);

        Assert.Equal(0, env.Run("/report"));
        var report = Path.Combine(env.DataFolder, "results.html");
        var html = File.ReadAllText(report);
        Assert.Contains("before vs after", html, StringComparison.Ordinal);
        Assert.Matches(@"File system:\s+1 new\s+1 modified\s+0 deleted", env.Text);

        // Names in either order; an absolute report path is used as given.
        var custom = Path.Combine(env.DataFolder, "Reports", "Office changes.html");
        Assert.Equal(0, env.Run("/report", "after", "oldest", custom));
        Assert.Contains("after vs oldest", File.ReadAllText(custom), StringComparison.Ordinal);

        Assert.Equal(2, env.Run("/report", "after", "missing"));
        Assert.Equal(1, env.Run("/report", "after", "after.stsnap"));
    }

    [Fact]
    public void CompareSnap_NeedsTwoSnapshots()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("only", day: 1);

        Assert.Equal(2, env.Run("/report"));
        Assert.Equal(2, env.Run("/export"));
    }

    [Fact]
    public void CompareSnap_UsesTheChosenIgnoreList()
    {
        using var env = new TestEnvironment { Ignore = [@"FS:C:\Windows\extra.dll"] };
        env.AddSnapshot("before", day: 2);
        env.AddSnapshot("after", day: 3, extraFile: true);

        Assert.Equal(0, env.Run("/report", "before", "after", "r.html"));
        Assert.DoesNotContain("extra.dll", File.ReadAllText(Path.Combine(env.DataFolder, "r.html")), StringComparison.Ordinal);
        Assert.Contains("Global Ignore list: local only, 1 rules", env.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DamagedOrNewerSnapshots_Exit4WithAClearMessage()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("good", day: 1);
        env.AddSnapshot("from the future", day: 2);
        var future = env.PathOf("from the future");
        var bytes = File.ReadAllBytes(future);
        bytes[8] = 2; // format major version 2
        File.WriteAllBytes(future, bytes);
        File.WriteAllBytes(env.PathOf("cut off"), File.ReadAllBytes(env.PathOf("good"))[..100]);

        Assert.Equal(4, env.Run("/report", "good", "from the future"));
        Assert.Contains("newer SysStateTracer", env.Text, StringComparison.Ordinal);
        Assert.Equal(4, env.Run("/export", "good", "cut off"));
        Assert.Contains("Error:", env.Text, StringComparison.Ordinal);

        // A damaged file can still be deleted by name.
        Assert.Equal(0, env.Run("/del", "cut off"));
        Assert.Contains("could not be read", env.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WorksWithoutAdministratorRights_WithANote()
    {
        using var env = new TestEnvironment { Elevated = false };
        env.AddSnapshot("before", day: 1);
        env.AddSnapshot("after", day: 2, extraFile: true);

        Assert.Equal(0, env.Run("/export"));
        Assert.Contains("not running as administrator", env.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_ListsNewestFirstAndReportsDamagedFiles()
    {
        using var env = new TestEnvironment();
        env.AddSnapshot("b", day: 1);
        env.AddSnapshot("a", day: 2);
        File.WriteAllText(env.PathOf("damaged"), "not a snapshot");
        var unreadable = new List<UnreadableSnapshot>();

        var list = new SnapshotCatalog(env.SnapshotFolder).List(unreadable);

        Assert.Equal(["a", "b"], list.Select(s => s.Name));
        Assert.Equal(env.PathOf("damaged"), Assert.Single(unreadable).Path);
        Assert.Equal(env.PathOf("a"), new SnapshotCatalog(env.SnapshotFolder).Find("A"));
        Assert.Null(new SnapshotCatalog(env.SnapshotFolder).Find("con"));
        Assert.Empty(new SnapshotCatalog(Path.Combine(env.SnapshotFolder, "none")).List());
    }

    /// <summary>A data folder, a separate snapshot folder, and captured output.</summary>
    private sealed class TestEnvironment : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly StringWriter _output = new();

        public TestEnvironment()
        {
            DataFolder = Directory.CreateDirectory(Path.Combine(_root.Path, "app")).FullName;
            SnapshotFolder = Directory.CreateDirectory(Path.Combine(_root.Path, "snapshots")).FullName;
        }

        public string DataFolder { get; }

        public string SnapshotFolder { get; }

        public bool Elevated { get; init; } = true;

        public string[] Ignore { get; init; } = [];

        public string Text => _output.ToString();

        public string PathOf(string name) => Path.Combine(SnapshotFolder, name + ".stsnap");

        public int Run(params string[] args)
        {
            _output.GetStringBuilder().Clear();
            var environment = new CliEnvironment
            {
                Paths = AppPaths.Resolve(DataFolder, Path.Combine(_root.Path, "fallback"), _ => true),
                Settings = new AppSettings { SnapshotFolder = SnapshotFolder, IgnoreListSource = IgnoreListSource.Local },
                Output = _output,
                IsElevated = Elevated,
                LoadIgnoreList = _ => new IgnoreListState(GlobalIgnoreList.Build(Ignore), IgnoreListOrigin.Local, "Test list."),
            };
            return CliRunner.Run(args, environment);
        }

        public void AddSnapshot(string name, int day, long notepadSize = 100, bool extraFile = false)
        {
            var names = new NameTable();
            var fs = new FsTreeBuilder(names);
            var c = fs.AddRoot("C:", FsTree.DirectoryAttribute, SampleSnapshots.Time(1));
            var windows = fs.Add(c, "Windows", FsTree.DirectoryAttribute, 0, SampleSnapshots.Time(1));
            fs.Add(windows, "notepad.exe", SampleSnapshots.Archive, notepadSize, SampleSnapshots.Time(1));
            if (extraFile)
            {
                fs.Add(windows, "extra.dll", SampleSnapshots.Archive, 5, SampleSnapshots.Time(day));
            }

            var meta = new SnapshotMeta
            {
                Name = name,
                StartedAt = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
                ComputerName = "PC",
            };
            var snapshot = new Snapshot(meta, names, fs.Build());
            names.Freeze();
            SnapshotWriter.Write(PathOf(name), snapshot);
        }

        public void Dispose()
        {
            _output.Dispose();
            _root.Dispose();
        }
    }
}
