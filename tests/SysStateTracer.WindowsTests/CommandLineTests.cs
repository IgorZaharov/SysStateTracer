using System.Text;
using Microsoft.Win32;
using SysStateTracer.Core.CommandLine;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Snapshots;

namespace SysStateTracer.WindowsTests;

/// <summary>Plan Step 40: /new-snap and /export on the live disk and registry (the other switches are in Core.Tests).</summary>
public class CommandLineTests
{
    [Fact]
    public void NewSnap_DefaultAndGivenName_SavedScope_DuplicateRefused()
    {
        using var test = new TestRegistryKey();
        using var data = new TestFolder();
        using var app = new CliApp(data.Path, test.HkcuPath);
        data.CreateFile(@"docs\readme.txt", 10);
        test.Key.SetValue("v", 1);

        Assert.Equal(0, app.Run("/new-snap"));
        var first = Assert.Single(new SnapshotCatalog(app.SnapshotFolder).List());
        Assert.Matches(@"^\d{8}_\d{2}-\d{2}-\d{2}_" + Environment.MachineName + "$", first.Name);
        Assert.Contains("Snapshot saved: " + first.Path, app.Text, StringComparison.Ordinal);
        Assert.Contains("Full scan", app.Text, StringComparison.Ordinal);
        Assert.Contains(first.Meta.Scope, s => s.TrimEnd('\\').Equals(data.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(test.StoredPath, first.Meta.Scope, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Services", first.Meta.Scope);
        Assert.Equal(1, first.Meta.Counts.Files);

        Assert.Equal(0, app.Run("/new-snap", "Before printer driver update"));
        Assert.True(File.Exists(Path.Combine(app.SnapshotFolder, "Before printer driver update.stsnap")));

        Assert.Equal(1, app.Run("/new-snap", "Before printer driver update"));

        // A path puts the snapshot in that folder (F6).
        var byPath = Path.Combine(app.DataFolder, "other folder", "Taken by path.stsnap");
        Assert.Equal(0, app.Run("/new-snap", byPath));
        Assert.True(File.Exists(byPath));

        Assert.Equal(1, app.Run("/new-snap", "bad|name"));
        Assert.Equal(2, new SnapshotCatalog(app.SnapshotFolder).List().Count);
        Assert.Single(new SnapshotCatalog(Path.Combine(app.DataFolder, "other folder")).List());
    }

    /// <summary>/new-snap reads the Global Ignore list chosen in Preferences: with Remote, the remote rules apply.</summary>
    [Fact]
    public void NewSnap_UsesTheChosenRemoteList()
    {
        using var test = new TestRegistryKey();
        using var data = new TestFolder();
        using var app = new CliApp(data.Path, test.HkcuPath);
        data.CreateFile(@"keep\a.txt", 5);
        data.CreateFile(@"skip-me\b.txt", 5);
        test.Key.SetValue("kept", 1);
        test.Key.SetValue("secret", 2);
        var remoteList = Path.Combine(app.DataFolder, "shared-ignore-list.txt");
        File.WriteAllText(remoteList, $@"FS:{data.Path}\skip-me{Environment.NewLine}REG:{test.StoredPath}\secret");
        app.Settings.IgnoreListSource = IgnoreListSource.Remote;
        app.Settings.IgnoreListRemoteLocation = remoteList;

        Assert.Equal(0, app.Run("/new-snap", "With remote rules"));
        Assert.Contains("Global Ignore list: local + remote, 2 rules", app.Text, StringComparison.Ordinal);

        var snapshot = Core.Storage.SnapshotReader.Load(Path.Combine(app.SnapshotFolder, "With remote rules.stsnap"));
        var files = Enumerable.Range(0, snapshot.FileSystem.Count).Select(i => snapshot.FileSystem.GetFullPath(i, snapshot.Names)).ToList();
        Assert.Contains(Path.Combine(data.Path, @"keep\a.txt"), files, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(files, f => f.Contains("skip-me", StringComparison.OrdinalIgnoreCase));
        var values = Enumerable.Range(0, snapshot.Registry.ValueCount).Select(v => snapshot.Names.GetString(snapshot.Registry.ValueName[v])).ToList();
        Assert.Contains("kept", values);
        Assert.DoesNotContain("secret", values);
        Assert.Equal(2, snapshot.Meta.IgnoreRuleCount);
    }

    /// <summary>
    /// /export leaves out what the Global Ignore list chosen in Preferences matches: single files and values,
    /// and also folders, files, keys and values inside a new folder or key (which the comparison keeps as one node).
    /// </summary>
    [Fact]
    public void Export_SkipsItemsInTheChosenGlobalIgnoreList()
    {
        using var test = new TestRegistryKey();
        using var data = new TestFolder();
        using var app = new CliApp(data.Path, test.HkcuPath);
        data.CreateFile(@"keep\same.txt", 5);
        test.Key.SetValue("changed", "old");
        Assert.Equal(0, app.Run("/new-snap", "Before app install"));

        data.CreateFile(@"keep\new.txt", 7);
        data.CreateFile(@"keep\debug.tmp", 7);                 // ignored by a wildcard rule
        data.CreateFile(@"NewApp\app.exe", 9);
        data.CreateFile(@"NewApp\logs\run.log", 3);            // ignored folder inside a new folder
        test.Key.SetValue("changed", "new");
        test.Key.SetValue("secret", "ignored value");          // ignored value
        using (var plugin = test.Key.CreateSubKey("Plugin"))
        {
            plugin.SetValue("v", "exported");
            plugin.SetValue("token", "ignored value in a new key");
            using var cache = plugin.CreateSubKey("Cache");    // ignored key inside a new key
            cache.SetValue("c", 1);
        }

        Assert.Equal(0, app.Run("/new-snap", "After app install"));

        var rules = string.Join(
            Environment.NewLine,
            $@"FS:{data.Path}\keep\*.tmp",
            $@"FS:{data.Path}\NewApp\logs",
            $@"REG:{test.StoredPath}\secret",
            $@"REG:{test.StoredPath}\Plugin\token",
            $@"REG:{test.StoredPath}\Plugin\Cache");
        var remoteList = Path.Combine(app.DataFolder, "shared-ignore-list.txt");
        File.WriteAllText(remoteList, rules);

        // Local + remote: the remote rules apply (the local list is empty).
        app.Settings.IgnoreListSource = IgnoreListSource.Remote;
        app.Settings.IgnoreListRemoteLocation = remoteList;
        Assert.Equal(0, app.Run("/export", "Before app install", "After app install"));
        Assert.Contains("Global Ignore list: local + remote, 5 rules", app.Text, StringComparison.Ordinal);

        var reg = app.ReadRegExport("Export_Before app install_vs_After app install.reg");
        Assert.Contains("\"changed\"=\"new\"", reg, StringComparison.Ordinal);
        Assert.Contains("\\Plugin]", reg, StringComparison.Ordinal);
        Assert.Contains("\"v\"=\"exported\"", reg, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", reg, StringComparison.Ordinal);
        Assert.DoesNotContain("token", reg, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache", reg, StringComparison.Ordinal);

        Assert.True(app.Exported(data.Path, @"keep\new.txt"));
        Assert.True(app.Exported(data.Path, @"NewApp\app.exe"));
        Assert.False(app.Exported(data.Path, @"keep\debug.tmp"));
        Assert.False(app.Exported(data.Path, @"NewApp\logs\run.log"));
        Assert.False(app.Exported(data.Path, @"keep\same.txt"));

        // Local only: the remote rules no longer apply, so everything new is exported.
        Directory.Delete(Path.Combine(app.DataFolder, AppPaths.FilesFolderName), recursive: true);
        app.Settings.IgnoreListSource = IgnoreListSource.Local;
        Assert.Equal(0, app.Run("/export", "Before app install", "After app install"));
        Assert.Contains("Global Ignore list: local only, 0 rules", app.Text, StringComparison.Ordinal);

        reg = app.ReadRegExport("Export_Before app install_vs_After app install.reg");
        Assert.Contains("secret", reg, StringComparison.Ordinal);
        Assert.Contains("token", reg, StringComparison.Ordinal);
        Assert.Contains("\\Plugin\\Cache]", reg, StringComparison.Ordinal);
        Assert.True(app.Exported(data.Path, @"keep\debug.tmp"));
        Assert.True(app.Exported(data.Path, @"NewApp\logs\run.log"));
    }

    [Fact]
    public void Export_NewestTwoByDefault_ReportsLockedFiles()
    {
        using var test = new TestRegistryKey();
        using var data = new TestFolder();
        using var app = new CliApp(data.Path, test.HkcuPath);
        Assert.Equal(0, app.Run("/new-snap", "Monday"));
        var locked = data.CreateFile("in-use.db", 10);
        data.CreateFile("report.docx", 10);
        Assert.Equal(0, app.Run("/new-snap", "Tuesday"));

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, app.Run("/export"));
        }

        Assert.Contains("Registry: nothing new or modified to export.", app.Text, StringComparison.Ordinal);
        Assert.Contains("1 copied", app.Text, StringComparison.Ordinal);
        Assert.Contains("Skipped " + locked, app.Text, StringComparison.Ordinal);
        Assert.True(app.Exported(data.Path, "report.docx"));
        Assert.False(File.Exists(Path.Combine(app.DataFolder, "Export_Monday_vs_Tuesday.reg")));
    }

    [Fact]
    public void Cancelled_Exit5()
    {
        using var test = new TestRegistryKey();
        using var data = new TestFolder();
        using var app = new CliApp(data.Path, test.HkcuPath);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Equal(5, app.Run(cancel.Token, "/new-snap", "Never finished"));
        Assert.Empty(new SnapshotCatalog(app.SnapshotFolder).List());
    }

    /// <summary>A program folder with an empty local ignore list, a snapshot folder, and the saved scope.</summary>
    private sealed class CliApp : IDisposable
    {
        private readonly TestFolder _root = new();
        private readonly StringWriter _output = new();

        public CliApp(string folder, string registryKey)
        {
            DataFolder = _root.CreateFolder("app");
            SnapshotFolder = _root.CreateFolder("snapshots");

            // The built-in defaults ignore %TEMP%, where the test folders are.
            File.WriteAllText(Path.Combine(DataFolder, AppPaths.LocalIgnoreFileName), "# no rules" + Environment.NewLine);
            Settings = new AppSettings
            {
                SnapshotFolder = SnapshotFolder,
                IgnoreListSource = IgnoreListSource.Local,
                Scope = new SnapshotScope { FileSystemRoots = [folder], RegistryRoots = [registryKey], IncludeServices = false },
            };
        }

        public string DataFolder { get; }

        public string SnapshotFolder { get; }

        public AppSettings Settings { get; }

        public string Text => _output.ToString();

        public int Run(params string[] args) => Run(CancellationToken.None, args);

        public int Run(CancellationToken cancellationToken, params string[] args)
        {
            _output.GetStringBuilder().Clear();
            var environment = new CliEnvironment
            {
                Paths = AppPaths.Resolve(DataFolder, Path.Combine(_root.Path, "fallback"), _ => true),
                Settings = Settings,
                Output = _output,
            };
            return CliRunner.Run(args, environment, cancellationToken);
        }

        public string ReadRegExport(string fileName) => File.ReadAllText(Path.Combine(DataFolder, fileName), Encoding.Unicode);

        /// <summary>True when <paramref name="relative"/> inside <paramref name="folder"/> was copied to Files\.</summary>
        public bool Exported(string folder, string relative)
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory)!;
            var target = folder.StartsWith(system, StringComparison.OrdinalIgnoreCase)
                ? folder[system.Length..]
                : "_" + folder[0] + folder[2..];
            return File.Exists(Path.Combine(DataFolder, AppPaths.FilesFolderName, target, relative));
        }

        public void Dispose()
        {
            _output.Dispose();
            _root.Dispose();
        }
    }
}
