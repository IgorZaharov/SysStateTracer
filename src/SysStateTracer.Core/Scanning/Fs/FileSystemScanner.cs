using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning.Fs.Ntfs;

namespace SysStateTracer.Core.Scanning.Fs;

public sealed class FileSystemScanOptions
{
    /// <summary>Folders to scan with everything below them, e.g. "C:\" or "D:\Data".</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public IgnoreMatcher Ignore { get; init; } = IgnoreMatcher.Empty;

    /// <summary>Folders listed at the same time; 0 = automatic.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    public FileSystemScanMethod Method { get; init; } = FileSystemScanMethod.Automatic;
}

public enum FileSystemScanMethod
{
    /// <summary>Master file table for whole NTFS drives when running as administrator; folder listing otherwise.</summary>
    Automatic,

    /// <summary>Always list folder by folder.</summary>
    ListFolders,

    /// <summary>Master file table for every root on NTFS (also single folders); folder listing where it cannot be used.</summary>
    MasterFileTable,
}

/// <summary>
/// Full file-system scan (plan §8.2). Worker threads list folders in bulk with
/// GetFileInformationByHandleEx (FileIdBothDirectoryInfo, 64 KB at a time) and add each folder's
/// entries to the shared builder in one short locked batch. File contents are never opened.
/// Folder links (junctions, symbolic links) are recorded where they are; their contents are read through
/// the link only when it leads outside the folders being scanned — e.g. a Desktop redirected to a network
/// share (GitHub issue #2) — and recorded under the link's own path. Links that point inside the scanned
/// folders are not followed, so nothing is read twice and link loops cannot occur. Other reparse folders,
/// such as cloud-file placeholders that are available on this PC, are read like ordinary folders.
/// </summary>
public sealed partial class FileSystemScanner
{
    private const int ListBufferSize = 64 * 1024;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;

    private readonly FsTreeBuilder _builder;
    private readonly Lock _builderLock;
    private readonly SkippedCollector _skipped;
    private readonly Dictionary<string, int> _chainIndex = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<string> _methods = new();

    /// <summary>Folders that are reparse points, found while scanning; handled by <see cref="FollowLinks"/>.</summary>
    private readonly ConcurrentQueue<(int Index, string Path)> _links = new();

    private long _entries;
    private int _pending;
    private string? _currentPath;

    /// <param name="builder">Receives the entries.</param>
    /// <param name="builderLock">Guards <paramref name="builder"/> and its name table, which other scanners may share.</param>
    /// <param name="skipped">Receives folders that could not be listed.</param>
    public FileSystemScanner(FsTreeBuilder builder, Lock builderLock, SkippedCollector skipped)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(skipped);
        _builder = builder;
        _builderLock = builderLock ?? throw new ArgumentNullException(nameof(builderLock));
        _skipped = skipped;
    }

    /// <summary>Files and folders recorded so far.</summary>
    public long EntriesScanned => Interlocked.Read(ref _entries);

    /// <summary>A folder being listed right now (for progress display).</summary>
    public string? CurrentPath => Volatile.Read(ref _currentPath);

    public void Scan(FileSystemScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        FindForeignFolders(NormalizeRoots(options.Roots), keepTables: true, cancellationToken);
        ScanCore(options, cancellationToken);
        FollowLinks(options, cancellationToken);
    }

    private void ScanCore(FileSystemScanOptions options, CancellationToken cancellationToken)
    {
        ProcessPrivileges.TryEnable(ProcessPrivileges.Backup);

        var listRoots = new List<string>();
        var tableRoots = new Dictionary<char, List<string>>();
        foreach (var root in NormalizeRoots(options.Roots))
        {
            if (!Directory.Exists(root))
            {
                _skipped.Add(root, Win32Errors.PathNotFound);
                continue;
            }

            if (options.Ignore.IsIgnored(IgnoreTarget.FileSystem, root))
            {
                continue;
            }

            if (UseFileTable(root, options.Method))
            {
                if (!tableRoots.TryGetValue(root[0], out var list))
                {
                    tableRoots[root[0]] = list = [];
                }

                list.Add(root);
            }
            else
            {
                listRoots.Add(root);
                _methods.Enqueue($"{root}: folder listing");
            }
        }

        // Each NTFS drive reads its file table on its own thread while other roots are listed.
        var tableTasks = tableRoots
            .Select(g => Task.Factory.StartNew(
                () => ScanWithFileTable(g.Key, g.Value, options.Ignore, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        var listTask = Task.Run(() => ListRoots(listRoots, options, cancellationToken), cancellationToken);

        Wait([.. tableTasks, listTask], cancellationToken);

        // Roots whose file table could not be used are listed folder by folder after all.
        var fallback = tableTasks.SelectMany(t => t.Result).ToList();
        if (fallback.Count > 0)
        {
            ListRoots(fallback, options, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>How each root was read, e.g. "C:\: master file table" (for the snapshot's details).</summary>
    public IReadOnlyList<string> Methods => [.. _methods];

    private static bool UseFileTable(string root, FileSystemScanMethod method)
    {
        switch (method)
        {
            case FileSystemScanMethod.ListFolders:
                return false;
            case FileSystemScanMethod.MasterFileTable:
                return true;
            default:
                // Whole NTFS drives only: for a single folder, reading the drive's whole table costs more.
                if (root.Length != 3 || !ProcessPrivileges.IsElevated)
                {
                    return false;
                }

                try
                {
                    return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
                }
                catch (IOException)
                {
                    return false;
                }
        }
    }

    private static void Wait(Task[] tasks, CancellationToken cancellationToken)
    {
        try
        {
            Task.WaitAll(tasks, CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            var cancelled = ex.Flatten().InnerExceptions.OfType<OperationCanceledException>().FirstOrDefault();
            if (cancelled is not null)
            {
                throw new OperationCanceledException("The file-system scan was stopped.", cancelled, cancellationToken);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Lists <paramref name="roots"/> folder by folder with worker threads.</summary>
    private void ListRoots(List<string> roots, FileSystemScanOptions options, CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
        {
            return;
        }

        ListFolders([.. roots.Select(root => new DirectoryJob(EnsureChain(root), TrimForJoin(root)))], options, cancellationToken);
    }

    /// <summary>Lists already-added folders and everything below them with worker threads.</summary>
    private void ListFolders(List<DirectoryJob> folders, FileSystemScanOptions options, CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return;
        }

        using var jobs = new BlockingCollection<DirectoryJob>(new ConcurrentQueue<DirectoryJob>());
        foreach (var job in folders)
        {
            Interlocked.Increment(ref _pending);
            jobs.Add(job, cancellationToken);
        }

        var workers = options.MaxDegreeOfParallelism > 0
            ? options.MaxDegreeOfParallelism
            : Math.Clamp(Environment.ProcessorCount * 2, 2, 16);

        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
        {
            tasks[w] = Task.Factory.StartNew(
                () => Work(jobs, options.Ignore, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Wait(tasks, cancellationToken);
    }

    /// <summary>
    /// Reads the drive's master file table once and adds each root's subtree from it.
    /// Returns the roots that must be listed folder by folder instead.
    /// </summary>
    private List<string> ScanWithFileTable(char drive, List<string> roots, IgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        var display = $@"{drive}:\ (file table)";
        Volatile.Write(ref _currentPath, display);
        long reported = 0;
        string? reason = null;
        var index = _preloadedTables.TryRemove(char.ToUpperInvariant(drive), out var preloaded) ? preloaded : MftIndex.TryLoad(
            drive,
            inUse =>
            {
                Interlocked.Add(ref _entries, inUse - reported);
                reported = inUse;
            },
            cancellationToken,
            out reason);

        if (index is null)
        {
            foreach (var root in roots)
            {
                _methods.Enqueue($"{root}: folder listing (file table not used: {reason})");
            }

            return roots;
        }

        var fallback = new List<string>();
        var state = new WorkerState();
        foreach (var root in roots)
        {
            var record = root.Length == 3 ? MftIndex.RootRecord : index.Find(root.AsSpan(3));
            if (record < 0)
            {
                _methods.Enqueue($"{root}: folder listing (not found in the file table)");
                fallback.Add(root);
                continue;
            }

            _methods.Enqueue($"{root}: master file table");
            AddFromFileTable(index, record, EnsureChain(root), TrimForJoin(root), ignore, state, cancellationToken);
        }

        return fallback;
    }

    /// <summary>Adds everything below folder <paramref name="rootRecord"/>, one folder per locked batch.</summary>
    private void AddFromFileTable(
        MftIndex index, int rootRecord, int rootEntry, string rootPath, IgnoreMatcher ignore, WorkerState state, CancellationToken cancellationToken)
    {
        var folders = new Stack<(int Record, int Entry, string Path)>();
        folders.Push((rootRecord, rootEntry, rootPath));
        var keep = new List<int>();
        var descend = new List<(int Record, int Entry, int Link)>();

        while (folders.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _currentPath, folder.Path);

            keep.Clear();
            foreach (var link in index.Children(folder.Record))
            {
                if (!ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, state.JoinPath(folder.Path, index.Name(link))))
                {
                    keep.Add(link);
                }
            }

            descend.Clear();
            lock (_builderLock)
            {
                foreach (var link in keep)
                {
                    var record = index.LinkRecord(link);
                    var attributes = index.Attributes(record);
                    var entry = _builder.Add(folder.Entry, index.Name(link), attributes, index.Size(record), index.LastWriteTime(record), index.FileReference(record));
                    if (index.IsDirectory(record) &&
                        (attributes & (NativeMethods.FileAttributeReparsePoint | NativeMethods.FileAttributeRecallOnOpen)) == 0)
                    {
                        if (IsForeign(folder.Path, index.Name(link)))
                        {
                            // The file table holds only the local folder; its real contents are on another volume.
                            _links.Enqueue((entry, string.Concat(folder.Path, "\\", index.Name(link))));
                        }
                        else
                        {
                            descend.Add((record, entry, link));
                        }
                    }
                    else if (index.IsDirectory(record) && IsLink(attributes))
                    {
                        _links.Enqueue((entry, string.Concat(folder.Path, "\\", index.Name(link))));
                    }
                }
            }

            foreach (var (record, entry, link) in descend)
            {
                folders.Push((record, entry, string.Concat(folder.Path, "\\", index.Name(link))));
            }
        }
    }

    /// <summary>"C:" → "C:\", "c:\data\" → "C:\data"; nested roots are dropped (their parent covers them).</summary>
    internal static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var normalized = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r =>
            {
                var full = Path.GetFullPath(r.Trim().Length == 2 && r.Trim()[1] == ':' ? r.Trim() + "\\" : r.Trim());
                full = NativeMethods.GetLongPath(full); // real names instead of 8.3 short names
                full = char.ToUpperInvariant(full[0]) + full[1..];
                return full.Length > 3 ? full.TrimEnd('\\') : full;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)
            .ToList();

        var result = new List<string>();
        foreach (var root in normalized)
        {
            var covered = result.Any(parent =>
                root.StartsWith(TrimForJoin(parent) + "\\", StringComparison.OrdinalIgnoreCase));
            if (!covered)
            {
                result.Add(root);
            }
        }

        return result;
    }

    /// <summary>"C:\" → "C:" so children join as "C:" + "\" + name.</summary>
    private static string TrimForJoin(string path) => path.Length == 3 && path[1] == ':' ? path[..2] : path;

    /// <summary>Adds the drive root and the folders leading to a scan root (each once) and returns the root's index.</summary>
    private int EnsureChain(string root)
    {
        var drive = root[..2];
        var segments = root.Length > 3 ? root[3..].Split('\\') : [];

        lock (_builderLock)
        {
            if (!_chainIndex.TryGetValue(drive, out var parent))
            {
                var info = new DirectoryInfo(drive + "\\");
                parent = _builder.AddRoot(drive, (uint)info.Attributes, info.LastWriteTimeUtc.ToFileTimeUtc(), FileIdentity.TryRead(drive)?.FileId ?? 0);
                _chainIndex[drive] = parent;
            }

            var path = drive;
            foreach (var segment in segments)
            {
                path += "\\" + segment;
                if (!_chainIndex.TryGetValue(path, out var index))
                {
                    var info = new DirectoryInfo(path);
                    index = _builder.Add(parent, segment, (uint)info.Attributes, 0, info.LastWriteTimeUtc.ToFileTimeUtc(), FileIdentity.TryRead(path)?.FileId ?? 0);
                    _chainIndex[path] = index;
                }

                parent = index;
            }

            return parent;
        }
    }

    private void Work(BlockingCollection<DirectoryJob> jobs, IgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        var state = new WorkerState();
        foreach (var job in jobs.GetConsumingEnumerable(cancellationToken))
        {
            try
            {
                ListDirectory(job, state, ignore, jobs, cancellationToken);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    jobs.CompleteAdding();
                }
            }
        }
    }

    private void ListDirectory(
        DirectoryJob job, WorkerState state, IgnoreMatcher ignore, BlockingCollection<DirectoryJob> jobs, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _currentPath, job.Path);
        if (!ListLive(job.ReadPath ?? job.Path, state, cancellationToken))
        {
            return;
        }

        // Decide what to keep before taking the lock: ignore checks need the full path.
        for (var i = 0; i < state.Count; i++)
        {
            ref var entry = ref state.Entries[i];
            var name = state.Name(entry);
            var fullPath = state.JoinPath(job.Path, name);
            entry.Ignored = ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, fullPath);
        }

        var subdirectories = state.Subdirectories;
        subdirectories.Clear();
        lock (_builderLock)
        {
            for (var i = 0; i < state.Count; i++)
            {
                ref var entry = ref state.Entries[i];
                if (entry.Ignored)
                {
                    continue;
                }

                // Through a link the ids belong to another volume or a server: not kept, so the next
                // incremental snapshot cannot mistake them for ids on this drive.
                var index = _builder.Add(job.Index, state.Name(entry), entry.Attributes, entry.Size, entry.LastWriteTime, job.ReadPath is null ? entry.FileId : 0);
                if ((entry.Attributes & NativeMethods.FileAttributeDirectory) != 0 &&
                    (entry.Attributes & (NativeMethods.FileAttributeReparsePoint | NativeMethods.FileAttributeRecallOnOpen)) == 0)
                {
                    if (job.ReadPath is null && IsForeign(job.Path, state.Name(entry)))
                    {
                        _links.Enqueue((index, string.Concat(job.Path, "\\", state.Name(entry))));
                    }
                    else
                    {
                        subdirectories.Add((index, i));
                    }
                }
                else if ((entry.Attributes & NativeMethods.FileAttributeDirectory) != 0 && IsLink(entry.Attributes))
                {
                    _links.Enqueue((index, string.Concat(job.Path, "\\", state.Name(entry))));
                }
            }
        }

        Interlocked.Add(ref _entries, state.Count);

        foreach (var (index, i) in subdirectories)
        {
            var name = state.Name(state.Entries[i]);
            var childPath = string.Concat(job.Path, "\\", name);
            var childRead = job.ReadPath is null ? null : string.Concat(job.ReadPath, "\\", name);
            Interlocked.Increment(ref _pending);
            jobs.Add(new DirectoryJob(index, childPath, childRead), cancellationToken);
        }
    }

    /// <summary>Lists folder <paramref name="path"/> ("C:" for a drive root) into <paramref name="state"/>; false (and Skipped) on failure.</summary>
    private bool ListLive(string path, WorkerState state, CancellationToken cancellationToken)
    {
        var openPath = OpenPath(path);
        using var handle = NativeMethods.CreateFile(
            openPath,
            NativeMethods.FileListDirectory | NativeMethods.Synchronize,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            0);

        if (handle.IsInvalid)
        {
            _skipped.Add(path, Marshal.GetLastPInvokeError());
            return false;
        }

        state.Clear();
        return ReadEntries(handle, path, state, cancellationToken);
    }

    /// <summary>Reads all entries of an open folder into <paramref name="state"/>; false if listing failed.</summary>
    private unsafe bool ReadEntries(SafeFileHandle handle, string path, WorkerState state, CancellationToken cancellationToken)
    {
        var infoClass = NativeMethods.FileIdBothDirectoryInfo;
        var first = true;
        fixed (byte* buffer = state.Buffer)
        {
            while (true)
            {
                // Huge folders (e.g. WinSxS on a slow disk) take long to list: check for Stop per block.
                cancellationToken.ThrowIfCancellationRequested();
                if (!NativeMethods.GetFileInformationByHandleEx(handle, infoClass, buffer, ListBufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreFiles)
                    {
                        return true;
                    }

                    if (first && error == ErrorInvalidParameter && infoClass == NativeMethods.FileIdBothDirectoryInfo)
                    {
                        // Some file systems do not support file ids in listings.
                        infoClass = NativeMethods.FileFullDirectoryInfo;
                        continue;
                    }

                    _skipped.Add(path, error);
                    return false;
                }

                first = false;
                Parse(new ReadOnlySpan<byte>(buffer, ListBufferSize), infoClass, state);
            }
        }
    }

    private static void Parse(ReadOnlySpan<byte> buffer, int infoClass, WorkerState state)
    {
        // FILE_ID_BOTH_DIR_INFO: name at 104, file id at 96.  FILE_FULL_DIR_INFO: name at 68, no id.
        var nameOffset = infoClass == NativeMethods.FileIdBothDirectoryInfo ? 104 : 68;
        var offset = 0;
        while (true)
        {
            var record = buffer[offset..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(record);
            var lastWrite = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
            var size = BinaryPrimitives.ReadInt64LittleEndian(record[40..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
            var fileId = infoClass == NativeMethods.FileIdBothDirectoryInfo ? BinaryPrimitives.ReadInt64LittleEndian(record[96..]) : 0;
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(record[60..]);
            var name = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameBytes));

            if (!(name is "." or ".."))
            {
                state.Add(name, attributes, size, lastWrite, fileId);
            }

            if (next == 0)
            {
                return;
            }

            offset += next;
        }
    }

    /// <summary>A folder to list: recorded as <paramref name="Path"/>, read from <paramref name="ReadPath"/> when it lies behind a link.</summary>
    private readonly record struct DirectoryJob(int Index, string Path, string? ReadPath = null);

    private struct ListedEntry
    {
        public int NameStart;
        public int NameLength;
        public uint Attributes;
        public long Size;
        public long LastWriteTime;
        public long FileId;
        public bool Ignored;
    }

    /// <summary>Per-thread buffers, reused for every folder.</summary>
    private sealed class WorkerState
    {
        private char[] _names = new char[4096];
        private int _nameLength;
        private char[] _path = new char[1024];

        public byte[] Buffer { get; } = GC.AllocateArray<byte>(ListBufferSize, pinned: true);

        public ListedEntry[] Entries { get; private set; } = new ListedEntry[256];

        public int Count { get; private set; }

        public List<(int Index, int Entry)> Subdirectories { get; } = [];

        public void Clear()
        {
            Count = 0;
            _nameLength = 0;
        }

        public void Add(ReadOnlySpan<char> name, uint attributes, long size, long lastWrite, long fileId)
        {
            if (Count == Entries.Length)
            {
                var grown = Entries;
                Array.Resize(ref grown, Entries.Length * 2);
                Entries = grown;
            }

            if (_nameLength + name.Length > _names.Length)
            {
                Array.Resize(ref _names, Math.Max(_names.Length * 2, _nameLength + name.Length));
            }

            name.CopyTo(_names.AsSpan(_nameLength));
            Entries[Count++] = new ListedEntry
            {
                NameStart = _nameLength,
                NameLength = name.Length,
                Attributes = attributes,
                Size = (attributes & NativeMethods.FileAttributeDirectory) != 0 ? 0 : size,
                LastWriteTime = lastWrite,
                FileId = fileId,
            };
            _nameLength += name.Length;
        }

        public ReadOnlySpan<char> Name(in ListedEntry entry) => _names.AsSpan(entry.NameStart, entry.NameLength);

        /// <summary>parent + "\" + name in a reused buffer (valid until the next call).</summary>
        public ReadOnlySpan<char> JoinPath(string parent, ReadOnlySpan<char> name)
        {
            var length = parent.Length + 1 + name.Length;
            if (length > _path.Length)
            {
                _path = new char[Math.Max(length, _path.Length * 2)];
            }

            parent.CopyTo(_path);
            _path[parent.Length] = '\\';
            name.CopyTo(_path.AsSpan(parent.Length + 1));
            return _path.AsSpan(0, length);
        }
    }
}
