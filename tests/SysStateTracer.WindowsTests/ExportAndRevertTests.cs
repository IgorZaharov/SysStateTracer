using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using SysStateTracer.Core.Actions;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.WindowsTests;

/// <summary>Plan Steps 33–38 against the live registry and disk: .reg export, Revert Value, copying files.</summary>
public class ExportAndRevertTests
{
    [Fact]
    public void RegExport_ImportedWithRegExe_GivesTheSameValues()
    {
        using var test = new TestRegistryKey();
        using var output = new TestFolder();
        test.Key.SetValue("keep", "old");
        var before = Take(test.HkcuPath, output);

        test.Key.SetValue("keep", "new");
        test.Key.SetValue(string.Empty, "default");
        test.Key.SetValue("expand", @"%windir%\x", RegistryValueKind.ExpandString);
        test.Key.SetValue("multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
        test.Key.SetValue("dword", unchecked((int)0xDEADBEEF), RegistryValueKind.DWord);
        test.Key.SetValue("qword", 0x0102030405060708L, RegistryValueKind.QWord);
        test.Key.SetValue("binary", Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), RegistryValueKind.Binary);
        test.Key.SetValue("none", Array.Empty<byte>(), RegistryValueKind.None);
        test.Key.SetValue("lines", "a\r\nb");
        test.Key.SetValue("quote\"and\\slash ✓", "say \"hi\" \\ there");
        using (var sub = test.Key.CreateSubKey(@"sub\deeper"))
        {
            sub.SetValue("v", 7);
        }

        test.Key.CreateSubKey("empty").Dispose();
        var after = Take(test.HkcuPath, output);

        var diff = SnapshotComparer.Compare(before, after);
        var (file, notStored) = DiffExport.Registry(diff, diff.Registry.Roots());
        Assert.Empty(notStored);
        var regPath = Path.Combine(output.Path, "export.reg");
        file.Save(regPath);

        Registry.CurrentUser.DeleteSubKeyTree(test.RelativePath);
        RunReg("import", regPath);
        var imported = Take(test.HkcuPath, output);

        var check = SnapshotComparer.Compare(after, imported);
        Assert.Equal(0, check.RegistryCounts.Total);
        Assert.Equal(after.Registry.KeyCount, imported.Registry.KeyCount);
        Assert.Equal(after.Registry.ValueCount, imported.Registry.ValueCount);
    }

    [Fact]
    public void Revert_RestoresTheOlderValuesAndWritesABackupThatUndoesIt()
    {
        using var test = new TestRegistryKey();
        using var output = new TestFolder();
        test.Key.SetValue("changed", "old");
        test.Key.SetValue("removed", 1);
        test.Key.SetValue("same", "same");
        using (var gone = test.Key.CreateSubKey("gone"))
        {
            gone.SetValue("g", "was here");
        }

        var older = Take(test.HkcuPath, output);

        test.Key.SetValue("changed", "new");
        test.Key.DeleteValue("removed");
        test.Key.SetValue("added", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        test.Key.DeleteSubKeyTree("gone");
        var newer = Take(test.HkcuPath, output);

        // Shown newer-first: the revert still goes to the snapshot taken first.
        var diff = SnapshotComparer.Compare(older, newer).Swap();
        Assert.Same(diff.Older, RegistryReverter.Target(diff));
        var tree = diff.Registry;
        var nodes = Enumerable.Range(0, tree.Count).ToDictionary(n => diff.FullPath(tree, n), StringComparer.OrdinalIgnoreCase);
        var items = RegistryReverter.Plan(diff, [nodes[test.StoredPath], nodes[test.StoredPath + @"\gone"]]);
        Assert.Equal(4, items.Count);

        var backups = Path.Combine(output.Path, "Backups");
        var result = RegistryReverter.Apply(items, backups, older.Meta.Name);

        Assert.Equal(4, result.Succeeded);
        using (var key = Registry.CurrentUser.OpenSubKey(test.RelativePath)!)
        {
            Assert.Equal("old", key.GetValue("changed"));
            Assert.Equal(1, key.GetValue("removed"));
            Assert.Null(key.GetValue("added"));
            Assert.Equal("same", key.GetValue("same"));
            using var gone = key.OpenSubKey("gone");
            Assert.Equal("was here", gone?.GetValue("g"));
        }

        Assert.StartsWith("Revert_", Path.GetFileName(result.BackupPath), StringComparison.Ordinal);
        Assert.EndsWith("_to_" + older.Meta.Name + ".reg", result.BackupPath, StringComparison.Ordinal);
        var backup = File.ReadAllText(result.BackupPath, Encoding.Unicode);
        Assert.Contains("\"changed\"=\"new\"", backup, StringComparison.Ordinal);
        Assert.Contains("\"removed\"=-", backup, StringComparison.Ordinal);
        Assert.Contains("\"added\"=hex:01,02", backup, StringComparison.Ordinal);
        Assert.Contains("\"g\"=-", backup, StringComparison.Ordinal);

        // Importing the backup undoes the revert (the recreated key itself stays, empty).
        RunReg("import", result.BackupPath);
        var undone = Take(test.HkcuPath, output);
        var check = SnapshotComparer.Compare(newer, undone);
        Assert.Equal(1, check.RegistryCounts.Total);
        var added = Enumerable.Range(0, check.Registry.Count).Single(n => check.Registry.Change(n) != ChangeKind.Unchanged);
        Assert.Equal(ChangeKind.New, check.Registry.Change(added));
        Assert.Equal(test.StoredPath + @"\gone", check.FullPath(check.Registry, added), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revert_ReadsCurrentValues()
    {
        using var test = new TestRegistryKey();
        test.Key.SetValue("dword", 42);

        var live = RegistryReverter.ReadCurrent(new RevertItem(test.StoredPath, "dword", RevertAction.Restore, 0, [], null));
        var missing = RegistryReverter.ReadCurrent(new RevertItem(test.StoredPath, "nothing", RevertAction.Restore, 0, [], null));

        Assert.True(live.Exists);
        Assert.Equal(4u, live.Type);
        Assert.Equal(BitConverter.GetBytes(42), live.Data);
        Assert.False(missing.Exists);
    }

    [Fact]
    public void Copy_KeepsStructureAndTimes_AndSkipsLockedAndMissingFiles()
    {
        using var source = new TestFolder();
        using var target = new TestFolder();
        var a = source.CreateFile("a.txt", 100);
        var b = source.CreateFile(@"sub\b.bin", 3 * 1024 * 1024 + 5, FileAttributes.ReadOnly);
        var locked = source.CreateFile("locked.txt", 10);
        File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var files = new List<FileToCopy>
        {
            new(a, @"X\a.txt", 100),
            new(b, @"X\sub\b.bin", 3 * 1024 * 1024 + 5),
            new(locked, @"X\locked.txt", 10),
            new(source.Combine("missing.txt"), @"X\missing.txt", 1),
        };
        var reports = new List<CopyProgress>();

        CopyReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            report = FileCopier.Copy(files, target.Path, new SyncProgress<CopyProgress>(reports.Add));
        }

        Assert.False(report.Cancelled);
        Assert.Equal(2, report.Copied);
        Assert.Equal(100 + 3 * 1024 * 1024 + 5, report.BytesCopied);
        Assert.Collection(
            report.Skipped,
            s => Assert.StartsWith("in use", s.Reason, StringComparison.Ordinal),
            s => Assert.Equal("deleted since the snapshot", s.Reason));
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(target.Combine(@"X\a.txt")));
        Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(target.Combine(@"X\sub\b.bin")));
        Assert.Equal(File.GetLastWriteTimeUtc(a), File.GetLastWriteTimeUtc(target.Combine(@"X\a.txt")));
        Assert.True(File.GetAttributes(target.Combine(@"X\sub\b.bin")).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(target.Combine(@"X\locked.txt")));
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Equal(4, reports[^1].FilesDone);
    }

    [Fact]
    public void Copy_Cancelled_KeepsFinishedFilesAndRemovesThePartlyCopiedOne()
    {
        using var source = new TestFolder();
        using var target = new TestFolder();
        var first = source.CreateFile("first.txt", 10);
        var big = source.CreateFile("big.bin", 4 * 1024 * 1024);
        var files = new List<FileToCopy> { new(first, "first.txt", 10), new(big, "big.bin", 4 * 1024 * 1024) };
        using var cancel = new CancellationTokenSource();

        var report = FileCopier.Copy(
            files,
            target.Path,
            new SyncProgress<CopyProgress>(p =>
            {
                if (p.CurrentFile == big)
                {
                    cancel.Cancel();
                }
            }),
            cancel.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(1, report.Copied);
        Assert.True(File.Exists(target.Combine("first.txt")));
        Assert.False(File.Exists(target.Combine("big.bin")));
    }

    [Fact]
    public void FilesFromAComparison_CopyToFilesFolder()
    {
        using var data = new TestFolder();
        using var output = new TestFolder();
        data.CreateFile(@"keep\same.txt", 5);
        var older = TakeFiles(data.Path, output);

        data.CreateFile(@"keep\new.txt", 7);
        data.CreateFile(@"fresh\deep\x.bin", 9);
        var newer = TakeFiles(data.Path, output);

        var diff = SnapshotComparer.Compare(older, newer);
        var files = DiffExport.Files(diff, diff.FileSystem.Roots());
        var report = FileCopier.Copy(files, Path.Combine(output.Path, "Files"));

        Assert.Equal(2, report.Copied);
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var relative = data.Path.StartsWith(system, StringComparison.OrdinalIgnoreCase)
            ? data.Path[system.Length..]
            : "_" + data.Path[0] + data.Path[2..];
        Assert.True(File.Exists(Path.Combine(output.Path, "Files", relative, @"keep\new.txt")));
        Assert.True(File.Exists(Path.Combine(output.Path, "Files", relative, @"fresh\deep\x.bin")));
        Assert.False(File.Exists(Path.Combine(output.Path, "Files", relative, @"keep\same.txt")));

        // The restore script (F7) puts the copies back where they came from.
        var newFile = Path.Combine(data.Path, @"keep\new.txt");
        var deep = Path.Combine(data.Path, @"fresh\deep\x.bin");
        var original = File.ReadAllBytes(newFile);
        File.Delete(newFile);
        Directory.Delete(Path.Combine(data.Path, "fresh"), recursive: true);
        var script = RestoreScript.Write(Path.Combine(output.Path, "Files"));

        var preview = RunPowerShell(script, "-WhatIf");
        Assert.Equal(0, preview.Code);
        Assert.False(File.Exists(newFile));

        var run = RunPowerShell(script);
        Assert.True(run.Code == 0, run.Output);
        Assert.Contains("2 files copied back", run.Output, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(newFile));
        Assert.True(File.Exists(deep));
    }

    private static (int Code, string Output) RunPowerShell(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static Snapshot Take(string registryRoot, TestFolder output) =>
        Run(new SnapshotRequest
        {
            Name = "t_" + Guid.NewGuid().ToString("N")[..8],
            OutputDirectory = output.Path,
            RegistryRoots = [registryRoot],
            IncludeServices = false,
        });

    private static Snapshot TakeFiles(string folder, TestFolder output) =>
        Run(new SnapshotRequest
        {
            Name = "t_" + Guid.NewGuid().ToString("N")[..8],
            OutputDirectory = output.Path,
            FileSystemRoots = [folder],
            IncludeServices = false,
        });

    private static Snapshot Run(SnapshotRequest request) => SnapshotReader.Load(SnapshotOrchestrator.Run(request).Path);

    private static void RunReg(params string[] arguments)
    {
        var start = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"reg {string.Join(' ', arguments)} failed ({process.ExitCode}): {text}");
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
