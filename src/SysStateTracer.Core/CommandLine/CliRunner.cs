using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.CommandLine;

/// <summary>What a command-line run works with; tests replace the parts that touch the machine.</summary>
public sealed class CliEnvironment
{
    public required AppPaths Paths { get; init; }

    public required AppSettings Settings { get; init; }

    /// <summary>The console (or a test writer). Every line also goes to SysStateTracer.log.</summary>
    public required TextWriter Output { get; init; }

    public bool IsElevated { get; init; } = ProcessPrivileges.IsElevated;

    /// <summary>Loads the Global Ignore list chosen in Preferences; null = the real loader (F1).</summary>
    public Func<CancellationToken, IgnoreListState>? LoadIgnoreList { get; init; }

    /// <summary>How often long operations print a progress line.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Runs one command-line switch (plan §10.9, Step 40) and returns the exit code.</summary>
public static class CliRunner
{
    private const string ReportDefaultName = "results.html";

    public static int Run(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var run = new Session(environment, cancellationToken);
        Log.Info("Command line: " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));

        var parsed = CliParser.Parse(args);
        if (parsed.Command is null)
        {
            run.Error(parsed.Error!);
            run.Say("Run SysStateTracer.exe /? for help.");
            return (int)CliExitCode.BadArguments;
        }

        var code = run.Execute(parsed.Command);
        Log.Info($"Command line finished with exit code {(int)code} ({code}).");
        return (int)code;
    }

    private sealed class Session(CliEnvironment env, CancellationToken cancellationToken)
    {
        private readonly Lock _outputLock = new();

        private AppPaths Paths => env.Paths;

        private AppSettings Settings => env.Settings;

        private SnapshotCatalog Catalog => new(Paths.SnapshotDirectory(Settings));

        public CliExitCode Execute(CliCommand command)
        {
            try
            {
                if (command.Kind == CliCommandKind.Help)
                {
                    env.Output.Write(CliHelp.Text(Catalog.Folder, Paths.DataDirectory));
                    return CliExitCode.Success;
                }

                Say($"{AppInfo.Name} {AppInfo.Version}");
                if (command.Kind is CliCommandKind.NewSnap or CliCommandKind.Export && !env.IsElevated)
                {
                    Error($"{CliParser.SwitchName(command.Kind)} needs administrator rights. Run it from a console opened with \"Run as administrator\".");
                    return CliExitCode.NotElevated;
                }

                return command.Kind switch
                {
                    CliCommandKind.NewSnap => NewSnap(command.Names.FirstOrDefault()),
                    CliCommandKind.DeleteSnap => DeleteSnap(command.Names.FirstOrDefault()),
                    CliCommandKind.RenameSnap => RenameSnap(command.Names[0], command.Names[1]),
                    CliCommandKind.CompareSnap => CompareSnap(command.Names, command.ReportPath),
                    _ => ExportSnap(command.Names),
                };
            }
            catch (OperationCanceledException)
            {
                Error("Cancelled.");
                return CliExitCode.Cancelled;
            }
            catch (SnapshotFormatException ex)
            {
                Error(ex.Message);
                return CliExitCode.ReadWriteError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error(ex.Message);
                return CliExitCode.ReadWriteError;
            }
        }

        public void Say(string line)
        {
            lock (_outputLock)
            {
                env.Output.WriteLine(line);
                env.Output.Flush();
            }

            Log.Info("CLI: " + line);
        }

        public void Error(string line)
        {
            lock (_outputLock)
            {
                env.Output.WriteLine("Error: " + line);
                env.Output.Flush();
            }

            Log.Error("CLI: " + line);
        }

        private CliExitCode NewSnap(string? name)
        {
            name ??= SnapshotNaming.DefaultName();
            if (name.EndsWith(SnapshotNaming.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^SnapshotNaming.FileExtension.Length];
            }

            if (!SnapshotNaming.IsValidName(name))
            {
                Error($"\"{name}\" cannot be used as a snapshot name (it must be a valid file name).");
                return CliExitCode.BadArguments;
            }

            var catalog = Catalog;
            if (File.Exists(catalog.PathFor(name)))
            {
                Error($"A snapshot named \"{name}\" already exists in {catalog.Folder}.");
                return CliExitCode.BadArguments;
            }

            var ignore = LoadIgnoreList();
            var request = SnapshotRequest.ForScope(name, catalog.Folder, Settings.Scope, ignore.List, Paths.CacheFile);
            Say($"Taking snapshot \"{name}\"");
            Say("  Folders:  " + (request.FileSystemRoots.Count == 0 ? "(none)" : string.Join(", ", request.FileSystemRoots)));
            Say("  Registry: " + (request.RegistryRoots.Count == 0 ? "(none)" : string.Join(", ", request.RegistryRoots)));
            Say("  Services: " + (request.IncludeServices ? request.IncludeDrivers ? "services and drivers" : "yes" : "no"));

            var printer = new ScanPrinter(this, env.ProgressInterval);
            var result = SnapshotOrchestrator.Run(request, printer, cancellationToken);

            var meta = result.Meta;
            var counts = meta.Counts;
            Say($"Snapshot saved: {result.Path}");
            Say(string.Create(CultureInfo.InvariantCulture,
                $"  {counts.Folders:N0} folders, {counts.Files:N0} files, {counts.RegistryKeys:N0} registry keys, {counts.RegistryValues:N0} values, {counts.Services:N0} services"));
            if (counts.Skipped > 0)
            {
                Say(string.Create(CultureInfo.InvariantCulture, $"  {counts.Skipped:N0} items could not be read and were skipped (details in {AppPaths.LogFileName})."));
            }

            Say(string.Create(CultureInfo.InvariantCulture,
                $"  {(meta.Mode == SnapshotMode.Full ? "Full scan" : "Incremental (unchanged parts reused from the previous snapshot)")}, {meta.Timings.TotalMs / 1000.0:0.0} s, {ItemFormat.Size(new FileInfo(result.Path).Length)}"));
            return CliExitCode.Success;
        }

        private CliExitCode DeleteSnap(string? name)
        {
            var catalog = Catalog;
            string path;
            string description;
            if (name is null)
            {
                var newest = catalog.List().FirstOrDefault();
                if (newest is null)
                {
                    Error($"There are no snapshots in {catalog.Folder}.");
                    return CliExitCode.SnapshotNotFound;
                }

                path = newest.Path;
                description = Describe(newest);
            }
            else
            {
                var found = catalog.Find(name);
                if (found is null)
                {
                    Error($"No snapshot named \"{name}\" in {catalog.Folder}.");
                    return CliExitCode.SnapshotNotFound;
                }

                path = found;
                description = TryDescribe(found);
            }

            File.Delete(path);
            Say($"Deleted snapshot {description}: {path}");
            return CliExitCode.Success;
        }

        private CliExitCode RenameSnap(string oldName, string newName)
        {
            var catalog = Catalog;
            var path = catalog.Find(oldName);
            if (path is null)
            {
                Error($"No snapshot named \"{oldName}\" in {catalog.Folder}.");
                return CliExitCode.SnapshotNotFound;
            }

            if (newName.EndsWith(SnapshotNaming.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                newName = newName[..^SnapshotNaming.FileExtension.Length];
            }

            if (!SnapshotNaming.IsValidName(newName))
            {
                Error($"\"{newName}\" cannot be used as a snapshot name (it must be a valid file name).");
                return CliExitCode.BadArguments;
            }

            var target = Path.Combine(Path.GetDirectoryName(path)!, newName + SnapshotNaming.FileExtension);
            if (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                Error($"A snapshot named \"{newName}\" already exists.");
                return CliExitCode.BadArguments;
            }

            var renamed = SnapshotCatalog.Rename(path, newName);
            Say($"Renamed snapshot \"{Path.GetFileNameWithoutExtension(path)}\" to \"{newName}\": {renamed}");
            return CliExitCode.Success;
        }

        private CliExitCode CompareSnap(IReadOnlyList<string> names, string? reportPath)
        {
            var (code, diff) = LoadAndCompare(names);
            if (diff is null)
            {
                return code;
            }

            var report = Path.GetFullPath(reportPath ?? ReportDefaultName, Paths.DataDirectory);
            HtmlReport.Write(diff, report);
            Say($"Report written: {report}");
            return CliExitCode.Success;
        }

        private CliExitCode ExportSnap(IReadOnlyList<string> names)
        {
            var (code, diff) = LoadAndCompare(names);
            if (diff is null)
            {
                return code;
            }

            // Registry: new and modified keys and values, from the newer snapshot.
            var (regFile, notStored) = DiffExport.Registry(diff, diff.Registry.Roots());
            if (regFile.KeyCount == 0)
            {
                Say("Registry: nothing new or modified to export.");
            }
            else
            {
                var regPath = Path.Combine(Paths.DataDirectory, SafeFileName($"Export_{diff.Left.Meta.Name}_vs_{diff.Right.Meta.Name}.reg"));
                regFile.Save(regPath);
                Say(string.Create(CultureInfo.InvariantCulture, $"Registry: {regFile.KeyCount:N0} keys, {regFile.ValueCount:N0} values written to {regPath}"));
            }

            foreach (var value in notStored)
            {
                Say($"  Not exported (larger than 1 MB, its data is not in the snapshot): {value}");
            }

            // Files: new and modified files, copied from the disk to .\Files.
            var files = DiffExport.Files(diff, diff.FileSystem.Roots());
            if (files.Count == 0)
            {
                Say("Files: nothing new or modified to copy.");
                return CliExitCode.Success;
            }

            var totalBytes = files.Sum(f => f.Size);
            Say(string.Create(CultureInfo.InvariantCulture, $"Files: copying {files.Count:N0} files ({ItemFormat.Size(totalBytes)}) to {Paths.FilesDirectory}"));
            var report = FileCopier.Copy(files, Paths.FilesDirectory, new CopyPrinter(this, env.ProgressInterval), cancellationToken);
            Say(string.Create(CultureInfo.InvariantCulture, $"Files: {report.Copied:N0} copied ({ItemFormat.Size(report.BytesCopied)}), {report.Skipped.Count:N0} skipped."));
            const int ShownSkips = 20;
            foreach (var skip in report.Skipped.Take(ShownSkips))
            {
                Say($"  Skipped {skip.Source}: {skip.Reason}");
            }

            if (report.Skipped.Count > ShownSkips)
            {
                Say(string.Create(CultureInfo.InvariantCulture, $"  ... and {report.Skipped.Count - ShownSkips:N0} more; all are listed in {AppPaths.LogFileName}."));
                foreach (var skip in report.Skipped.Skip(ShownSkips))
                {
                    Log.Info($"CLI: Skipped {skip.Source}: {skip.Reason}");
                }
            }

            if (report.Cancelled)
            {
                Error("Cancelled; the files copied so far are kept.");
                return CliExitCode.Cancelled;
            }

            return CliExitCode.Success;
        }

        /// <summary>
        /// Loads the two snapshots (the newest two when no names are given) and compares them with the
        /// Global Ignore list in effect. The first name is shown on the left; exports use the newer one.
        /// </summary>
        private (CliExitCode Code, DiffResult? Diff) LoadAndCompare(IReadOnlyList<string> names)
        {
            var catalog = Catalog;
            string first, second;
            if (names.Count == 0)
            {
                var newest = catalog.Newest(2);
                if (newest.Count < 2)
                {
                    Error($"Two snapshots are needed; {catalog.Folder} has {newest.Count}.");
                    return (CliExitCode.SnapshotNotFound, null);
                }

                (first, second) = (newest[1].Path, newest[0].Path);
            }
            else
            {
                var a = catalog.Find(names[0]);
                var b = catalog.Find(names[1]);
                foreach (var (name, path) in new[] { (names[0], a), (names[1], b) })
                {
                    if (path is null)
                    {
                        Error($"No snapshot named \"{name}\" in {catalog.Folder}.");
                        return (CliExitCode.SnapshotNotFound, null);
                    }
                }

                if (string.Equals(Path.GetFullPath(a!), Path.GetFullPath(b!), StringComparison.OrdinalIgnoreCase))
                {
                    Error("Choose two different snapshots.");
                    return (CliExitCode.BadArguments, null);
                }

                (first, second) = (a!, b!);
            }

            var ignore = LoadIgnoreList();
            var watch = Stopwatch.StartNew();
            var left = SnapshotReader.Load(first, cancellationToken: cancellationToken);
            var right = SnapshotReader.Load(second, cancellationToken: cancellationToken);
            left.Meta.Name = Path.GetFileNameWithoutExtension(first);
            right.Meta.Name = Path.GetFileNameWithoutExtension(second);
            Say($"Comparing \"{left.Meta.Name}\" ({Stamp(left.Meta)}) with \"{right.Meta.Name}\" ({Stamp(right.Meta)})");

            var leftIsOlder = left.Meta.StartedAt <= right.Meta.StartedAt;
            var diff = leftIsOlder
                ? SnapshotComparer.Compare(left, right, ignore.List.Matcher, cancellationToken)
                : SnapshotComparer.Compare(right, left, ignore.List.Matcher, cancellationToken).Swap();
            Say(string.Create(CultureInfo.InvariantCulture, $"Compared in {watch.Elapsed.TotalSeconds:0.0} s; changes from \"{diff.Older.Meta.Name}\" to \"{diff.Newer.Meta.Name}\":"));
            Counts("File system", leftIsOlder ? diff.FileSystemCounts : Flip(diff.FileSystemCounts));
            Counts("Registry", leftIsOlder ? diff.RegistryCounts : Flip(diff.RegistryCounts));
            Counts("Services", leftIsOlder ? diff.ServicesCounts : Flip(diff.ServicesCounts));
            return (CliExitCode.Success, diff);

            void Counts(string label, DiffCounts counts) => Say(string.Create(
                CultureInfo.InvariantCulture,
                $"  {label + ":",-13}{counts.New,10:N0} new{counts.Modified,10:N0} modified{counts.Deleted,10:N0} deleted"));

            // Counts of a swapped result are from the left side's point of view; print them older → newer.
            static DiffCounts Flip(DiffCounts c) => c with { New = c.Deleted, Deleted = c.New };
        }

        private IgnoreListState LoadIgnoreList()
        {
            IgnoreListState state;
            if (env.LoadIgnoreList is { } load)
            {
                state = load(cancellationToken);
            }
            else
            {
                using var http = new HttpClient();
                state = IgnoreListLoader.LoadAsync(
                        Settings.IgnoreListSource,
                        Settings.IgnoreListRemoteLocation,
                        new LocalIgnoreFile(Paths.LocalIgnoreFile),
                        http,
                        cancellationToken: cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }

            var origin = state.Origin switch
            {
                IgnoreListOrigin.Remote => "remote",
                IgnoreListOrigin.LocalFallback => "local (remote not reachable)",
                _ => "local",
            };
            Say(string.Create(CultureInfo.InvariantCulture, $"Global Ignore list: {origin}, {state.List.Rules.Count:N0} rules. {state.Message}"));
            return state;
        }

        private static string Stamp(SnapshotMeta meta) =>
            meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Describe(SnapshotInfo info) => $"\"{info.Name}\" (taken {Stamp(info.Meta)})";

        private static string TryDescribe(string path)
        {
            try
            {
                return Describe(SnapshotReader.ReadInfo(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
            {
                return $"\"{Path.GetFileNameWithoutExtension(path)}\" (could not be read: {ex.Message})";
            }
        }

        private static string SafeFileName(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    /// <summary>Prints snapshot progress: each phase, and a line every few seconds while scanning.</summary>
    private sealed class ScanPrinter(Session run, TimeSpan interval) : IProgress<ScanProgress>
    {
        private readonly Lock _lock = new();
        private SnapshotPhase? _phase;
        private TimeSpan _last;

        public void Report(ScanProgress value)
        {
            lock (_lock)
            {
                var phaseChanged = value.Phase != _phase;
                if (!phaseChanged && value.Elapsed - _last < interval)
                {
                    return;
                }

                _phase = value.Phase;
                _last = value.Elapsed;
                var text = value.Phase switch
                {
                    SnapshotPhase.Starting => "Starting",
                    SnapshotPhase.Writing => "Writing the snapshot file",
                    SnapshotPhase.Done => "Done",
                    _ => string.Create(CultureInfo.InvariantCulture,
                        $"Scanning: {value.FileSystemEntries:N0} files and folders, {value.RegistryKeys:N0} registry keys, {value.Services:N0} services"),
                };
                run.Say(string.Create(CultureInfo.InvariantCulture, $"  [{value.Elapsed:mm\\:ss}] {text}"));
            }
        }
    }

    /// <summary>Prints file-copy progress every few seconds.</summary>
    private sealed class CopyPrinter(Session run, TimeSpan interval) : IProgress<CopyProgress>
    {
        private TimeSpan _last = TimeSpan.Zero;

        public void Report(CopyProgress value)
        {
            if (value.Elapsed - _last < interval)
            {
                return;
            }

            _last = value.Elapsed;
            run.Say(string.Create(CultureInfo.InvariantCulture,
                $"  [{value.Elapsed:mm\\:ss}] {value.FilesDone:N0} of {value.FilesTotal:N0} files, {ItemFormat.Size(value.BytesDone)} of {ItemFormat.Size(value.BytesTotal)} ({value.Fraction:P0})"));
        }
    }
}
