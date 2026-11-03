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
                // F9: a standard user can take snapshots and export too; what needs administrator rights is skipped.
                if (command.Kind is CliCommandKind.NewSnap or CliCommandKind.Export && !env.IsElevated)
                {
                    Say(command.Kind == CliCommandKind.NewSnap
                        ? "Note: not running as administrator. Folders and registry keys that need administrator rights are skipped, and whole drives are read more slowly. For a complete snapshot, run from a console opened with \"Run as administrator\"."
                        : "Note: not running as administrator. Files in protected folders cannot be copied.");
                }

                return command.Kind switch
                {
                    CliCommandKind.NewSnap => NewSnap(command.Names.FirstOrDefault()),
                    CliCommandKind.DeleteSnap => DeleteSnap(command.Names.FirstOrDefault()),
                    CliCommandKind.RenameSnap => RenameSnap(command.Names[0], command.Names[1]),
                    CliCommandKind.CompareSnap => CompareSnap(command),
                    _ => ExportSnap(command),
                };
            }
            catch (OperationCanceledException)
            {
                Error("Cancelled.");
                return CliExitCode.Cancelled;
            }
            catch (SnapshotFormatException ex)
            {
                Error(FriendlyErrors.Describe(ex));
                return CliExitCode.ReadWriteError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error(FriendlyErrors.Describe(ex));
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

        private CliExitCode NewSnap(string? argument)
        {
            if (!TryTarget(argument ?? SnapshotNaming.DefaultName(), out var folder, out var name))
            {
                return CliExitCode.BadArguments;
            }

            if (File.Exists(Path.Combine(folder, name + SnapshotNaming.FileExtension)))
            {
                Error($"A snapshot named \"{name}\" already exists in {folder}.");
                return CliExitCode.BadArguments;
            }

            var ignore = LoadIgnoreList();
            var request = SnapshotRequest.ForScope(name, folder, Settings.Scope, ignore.List, Paths.CacheFile);
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
                    Error(NotFound(name, catalog));
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
                Error(NotFound(oldName, catalog));
                return CliExitCode.SnapshotNotFound;
            }

            // A new name stays in the same folder; a path moves the snapshot there (F6).
            var isPath = IsPath(newName);
            if (!TryTarget(newName, out var folder, out var name, defaultFolder: Path.GetDirectoryName(path)!))
            {
                return CliExitCode.BadArguments;
            }

            var target = Path.Combine(folder, name + SnapshotNaming.FileExtension);
            if (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                Error($"A snapshot named \"{name}\" already exists in {folder}.");
                return CliExitCode.BadArguments;
            }

            string renamed;
            if (isPath && !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(folder);
                File.Move(path, target);
                renamed = target;
            }
            else
            {
                renamed = SnapshotCatalog.Rename(path, name);
            }

            Say($"Renamed snapshot \"{Path.GetFileNameWithoutExtension(path)}\" to \"{name}\": {renamed}");
            return CliExitCode.Success;
        }

        private CliExitCode CompareSnap(CliCommand command)
        {
            if (!TryFilter(command, out var filter))
            {
                return CliExitCode.BadArguments;
            }

            var report = Path.GetFullPath(command.ReportPath ?? ReportDefaultName, Paths.DataDirectory);
            if (!TryPrepareFolder(Path.GetDirectoryName(report)!, "report"))
            {
                return CliExitCode.ReadWriteError;
            }

            var (code, diff) = LoadAndCompare(command.Names);
            if (diff is null)
            {
                return code;
            }

            diff = ApplyFilter(diff, filter);
            HtmlReport.Write(diff, report);
            Say($"Report written: {report}");
            return CliExitCode.Success;
        }

        private CliExitCode ExportSnap(CliCommand command)
        {
            if (!TryFilter(command, out var filter))
            {
                return CliExitCode.BadArguments;
            }

            // Where it goes (issue #8): /output: is a folder, or the .reg file with Files beside it; the restore
            // script then sits beside the .reg file. Without /output: as before, the program folder and .\Files.
            string outputFolder, filesFolder;
            string? regPath = null, scriptFolder = null;
            if (command.OutputPath is { } output)
            {
                var full = Path.GetFullPath(output, Paths.DataDirectory);
                if (full.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
                {
                    regPath = full;
                    outputFolder = Path.GetDirectoryName(full)!;
                }
                else
                {
                    outputFolder = full;
                }

                filesFolder = Path.Combine(outputFolder, "Files");
                scriptFolder = outputFolder;
                if (!TryPrepareFolder(outputFolder, "output"))
                {
                    return CliExitCode.ReadWriteError;
                }

                Say($"Output folder: {outputFolder}");
            }
            else
            {
                outputFolder = Paths.DataDirectory;
                filesFolder = Paths.FilesDirectory;
            }

            var (code, diff) = LoadAndCompare(command.Names);
            if (diff is null)
            {
                return code;
            }

            diff = ApplyFilter(diff, filter);

            // Registry: new and modified keys and values, from the newer snapshot.
            if (filter is null || filter.Has(IgnoreTarget.Registry))
            {
                var (regFile, notStored) = DiffExport.Registry(diff, diff.Registry.Roots());
                if (regFile.KeyCount == 0)
                {
                    Say("Registry: nothing new or modified to export.");
                }
                else
                {
                    regPath ??= Path.Combine(outputFolder, SafeFileName($"Export_{diff.Left.Meta.Name}_vs_{diff.Right.Meta.Name}.reg"));
                    regFile.Save(regPath);
                    Say(string.Create(CultureInfo.InvariantCulture, $"Registry: {regFile.KeyCount:N0} keys, {regFile.ValueCount:N0} values written to {regPath}"));
                }

                foreach (var value in notStored)
                {
                    Say($"  Not exported (larger than 1 MB, its data is not in the snapshot): {value}");
                }
            }
            else
            {
                Say("Registry: not exported (no /registry: filter).");
            }

            // Files: new and modified files, copied from the disk to the Files folder.
            if (filter is not null && !filter.Has(IgnoreTarget.FileSystem))
            {
                Say("Files: not copied (no /files: filter).");
                return CliExitCode.Success;
            }

            var files = DiffExport.Files(diff, diff.FileSystem.Roots());
            if (files.Count == 0)
            {
                Say("Files: nothing new or modified to copy.");
                return CliExitCode.Success;
            }

            var totalBytes = files.Sum(f => f.Size);
            Say(string.Create(CultureInfo.InvariantCulture, $"Files: copying {files.Count:N0} files ({ItemFormat.Size(totalBytes)}) to {filesFolder}"));
            var report = FileCopier.Copy(files, filesFolder, new CopyPrinter(this, env.ProgressInterval), cancellationToken);
            Say(string.Create(CultureInfo.InvariantCulture, $"Files: {report.Copied:N0} copied ({ItemFormat.Size(report.BytesCopied)}), {report.Skipped.Count:N0} skipped."));
            if (report.Copied > 0)
            {
                var script = RestoreScript.Write(filesFolder, scriptFolder: scriptFolder);
                Say($"Restore script: {script} copies the files back to their places (try it with -WhatIf first).");
            }

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

        /// <summary>Reads /files:, /registry: and /services: (issue #8); null filter when none was given.</summary>
        private bool TryFilter(CliCommand command, out DiffFilter? filter)
        {
            filter = null;
            if (!command.HasFilters)
            {
                return true;
            }

            if (!DiffFilter.TryCreate(command.Files, command.Registry, command.Services, out var parsed, out var error))
            {
                Error(error);
                return false;
            }

            filter = parsed;
            Say("Filters (only these are compared" + (command.Kind == CliCommandKind.Export ? " and exported" : string.Empty) + "):");
            foreach (var entry in parsed.Entries)
            {
                var shown = entry.Target == IgnoreTarget.Registry && !string.Equals(entry.Text, entry.Rule.Pattern, StringComparison.OrdinalIgnoreCase)
                    ? $"{entry.Text}  (matched as {entry.Rule.Pattern})"
                    : entry.Text;
                Say($"  {Label(entry.Target),-13}{shown}");
            }

            return true;
        }

        /// <summary>Creates <paramref name="folder"/> if needed and checks it can be written to; says why not.</summary>
        private bool TryPrepareFolder(string folder, string what)
        {
            if (File.Exists(folder))
            {
                Error($"The {what} folder {folder} is a file. Give a folder, or a path that does not exist yet.");
                return false;
            }

            var existed = Directory.Exists(folder);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Error($"The {what} folder {folder} could not be created: {FriendlyErrors.Describe(ex)}");
                return false;
            }

            if (!AppPaths.IsDirectoryWritable(folder))
            {
                Error($"The {what} folder {folder} cannot be written to. Choose another folder, or run from a console opened with \"Run as administrator\".");
                return false;
            }

            if (!existed)
            {
                Say($"Created the {what} folder {folder}");
            }

            return true;
        }

        /// <summary>Keeps only what the filters name and says what each one matched.</summary>
        private DiffResult ApplyFilter(DiffResult diff, DiffFilter? filter)
        {
            if (filter is null)
            {
                return diff;
            }

            var filtered = filter.Apply(diff, cancellationToken);
            Say("After the filters:");
            foreach (var entry in filter.Entries)
            {
                Say(string.Create(CultureInfo.InvariantCulture, $"  {Label(entry.Target),-13}{entry.Text}: {entry.Matched:N0} changes"));
                if (entry.Matched == 0)
                {
                    Say($"    Warning: nothing new, modified or deleted matches {entry.Text}. Check the path; it is matched as {entry.Rule.Pattern}.");
                }
                else if (entry.WidenedToNewOrDeleted)
                {
                    Say($"    Note: {entry.Text} is inside a folder or key that is new or deleted as a whole; all of that folder or key is included.");
                }
            }

            PrintCounts(filtered);
            return filtered;
        }

        private static string Label(IgnoreTarget target) => target switch
        {
            IgnoreTarget.FileSystem => "File system:",
            IgnoreTarget.Registry => "Registry:",
            _ => "Services:",
        };

        private void PrintCounts(DiffResult diff)
        {
            // Counts of a swapped result are from the left side's point of view; print them older → newer.
            var leftIsOlder = !diff.FileSystem.IsSwapped;
            Counts("File system", leftIsOlder ? diff.FileSystemCounts : Flip(diff.FileSystemCounts));
            Counts("Registry", leftIsOlder ? diff.RegistryCounts : Flip(diff.RegistryCounts));
            Counts("Services", leftIsOlder ? diff.ServicesCounts : Flip(diff.ServicesCounts));

            void Counts(string label, DiffCounts counts) => Say(string.Create(
                CultureInfo.InvariantCulture,
                $"  {label + ":",-13}{counts.New,10:N0} new{counts.Modified,10:N0} modified{counts.Deleted,10:N0} deleted"));

            static DiffCounts Flip(DiffCounts c) => c with { New = c.Deleted, Deleted = c.New };
        }

        /// <summary>True when the argument is a path ("C:\\Test\\a.stsnap", "Test\\a", "a/b") rather than a name.</summary>
        private static bool IsPath(string argument) =>
            argument.Contains('\\', StringComparison.Ordinal) || argument.Contains('/', StringComparison.Ordinal) || argument.Contains(':', StringComparison.Ordinal);

        private static string NotFound(string argument, SnapshotCatalog catalog) => IsPath(argument)
            ? $"No snapshot file {Path.GetFullPath(argument)}."
            : $"No snapshot named \"{argument}\" in {catalog.Folder}.";

        /// <summary>
        /// Folder and name for a snapshot to create or rename to (F6): a name ("Before install", "Before install.stsnap")
        /// goes to <paramref name="defaultFolder"/> (the snapshot folder); a path ("C:\\Snapshots\\Before install.stsnap")
        /// goes where it says. Writes the error and returns false when the name is not usable.
        /// </summary>
        private bool TryTarget(string argument, out string folder, out string name, string? defaultFolder = null)
        {
            folder = defaultFolder ?? Catalog.Folder;
            name = argument.Trim();
            if (IsPath(name))
            {
                try
                {
                    var full = Path.GetFullPath(name);
                    folder = Path.GetDirectoryName(full) ?? folder;
                    name = Path.GetFileName(full);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    Error($"\"{argument}\" is not a valid path: {ex.Message}");
                    return false;
                }
            }

            if (name.EndsWith(SnapshotNaming.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^SnapshotNaming.FileExtension.Length];
            }

            if (!SnapshotNaming.IsValidName(name))
            {
                Error($"\"{name}\" cannot be used as a snapshot name (it must be a valid file name).");
                return false;
            }

            return true;
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
                        Error(NotFound(name, catalog));
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
            var (olderMeta, newerMeta) = left.Meta.StartedAt <= right.Meta.StartedAt ? (left.Meta, right.Meta) : (right.Meta, left.Meta);
            if (SnapshotMeta.RightsWarning(olderMeta, newerMeta) is { } rights)
            {
                Say("Note: " + rights);
            }

            var leftIsOlder = left.Meta.StartedAt <= right.Meta.StartedAt;
            var diff = leftIsOlder
                ? SnapshotComparer.Compare(left, right, ignore.List.Matcher, cancellationToken)
                : SnapshotComparer.Compare(right, left, ignore.List.Matcher, cancellationToken).Swap();
            Say(string.Create(CultureInfo.InvariantCulture, $"Compared in {watch.Elapsed.TotalSeconds:0.0} s; changes from \"{diff.Older.Meta.Name}\" to \"{diff.Newer.Meta.Name}\":"));
            PrintCounts(diff);
            return (CliExitCode.Success, diff);
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
                IgnoreListOrigin.Remote => "local + remote",
                IgnoreListOrigin.LocalFallback => "local only (remote not reachable)",
                _ => "local only",
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
                return $"\"{Path.GetFileNameWithoutExtension(path)}\" (could not be read: {FriendlyErrors.Describe(ex)})";
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
