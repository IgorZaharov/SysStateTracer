using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning.Fs.Ntfs;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>
/// Consecutive snapshots (plan §8.5 steps 4–7): the previous tree is copied, and only what the change
/// journal reports is read again. Folders whose entries may have changed are listed again; files and
/// folders that changed are re-read by file reference number (which also updates every hard link);
/// folders that appeared since are listed in full. Everything else comes from the previous tree.
/// </summary>
public sealed partial class FileSystemScanner
{
    private const uint NoDescend = NativeMethods.FileAttributeReparsePoint | NativeMethods.FileAttributeRecallOnOpen;

    /// <summary>
    /// Builds the tree from <paramref name="previous"/> plus the changes in <paramref name="changes"/> (per drive letter).
    /// The roots must be the ones <paramref name="previous"/> was scanned with.
    /// </summary>
    internal void ScanIncremental(
        FsTree previous,
        NameTable previousNames,
        IReadOnlyDictionary<char, UsnChanges> changes,
        FileSystemScanOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(previousNames);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(options);
        ProcessPrivileges.TryEnable(ProcessPrivileges.Backup);

        // Roots on drives without usable journal changes are scanned in full, at the same time as the walk.
        var roots = NormalizeRoots(options.Roots);
        var fullRoots = roots.Where(r => !changes.ContainsKey(char.ToUpperInvariant(r[0]))).ToList();
        var fullScan = fullRoots.Count == 0
            ? Task.CompletedTask
            : Task.Run(
                () => ScanCore(
                    new FileSystemScanOptions
                    {
                        Roots = fullRoots, Ignore = options.Ignore, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, Method = options.Method,
                    },
                    cancellationToken),
                cancellationToken);

        var walk = new IncrementalWalk(this, previous, previousNames, options.Ignore, cancellationToken);
        var newFolders = new List<(int Index, string Path)>();
        try
        {
            foreach (var root in roots.Except(fullRoots))
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

                var index = EnsureChain(root);
                var old = FindPrevious(previous, previousNames, root);
                var driveChanges = changes[char.ToUpperInvariant(root[0])];
                if (old < 0)
                {
                    _methods.Enqueue($"{root}: folder listing (not in the previous snapshot)");
                    newFolders.Add((index, TrimForJoin(root)));
                    continue;
                }

                var before = walk.FoldersRelisted;
                walk.Run(old, index, TrimForJoin(root), driveChanges);
                _methods.Enqueue($"{root}: incremental ({walk.FoldersRelisted - before:N0} folders read again, {driveChanges.Records:N0} journal records)");
            }
        }
        finally
        {
            walk.Dispose();
        }

        // Roots that are new since the previous snapshot: listed in full, in parallel.
        ListFolders([.. newFolders.Select(f => new DirectoryJob(f.Index, f.Path))], options, cancellationToken);
        Wait([fullScan], cancellationToken);

        // What lies behind folder links is read again every time: this drive's journal does not see it change.
        FollowLinks(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Entry of <paramref name="path"/> in <paramref name="tree"/> (e.g. "C:\Users"), or -1.</summary>
    internal static int FindPrevious(FsTree tree, NameTable names, string path)
    {
        var segments = TrimForJoin(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = -1;
        foreach (var segment in segments)
        {
            var start = current < 0 ? 0 : current + 1;
            var end = current < 0 ? tree.Count - 1 : current + tree.Descendants[current];
            var found = -1;
            for (var c = start; c <= end; c += tree.Descendants[c] + 1)
            {
                if (names[tree.Name[c]].Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }

            if (found < 0)
            {
                return -1;
            }

            current = found;
        }

        return current;
    }

    /// <summary>One incremental pass over the previous tree (single thread; adds to the builder under the lock).</summary>
    private sealed class IncrementalWalk(
        FileSystemScanner scanner, FsTree previous, NameTable previousNames, IgnoreMatcher ignore, CancellationToken cancellationToken) : IDisposable
    {
        private readonly WorkerState _state = new();
        private readonly Dictionary<char, SafeFileHandle> _volumeHints = [];
        private Dictionary<long, int>? _foldersById;

        public long FoldersRelisted { get; private set; }

        public void Run(int oldRoot, int newRoot, string rootPath, UsnChanges changes)
        {
            var hint = VolumeHint(rootPath[0]);
            var stack = new Stack<(int Old, int New, string Path, bool Moved)>();
            stack.Push((oldRoot, newRoot, rootPath, false));
            // Listed names are copied to strings; names taken from the previous tree stay ids (no allocation).
            var batch = new List<(string? Name, int NameId, FileMetadata Meta, int Old, bool Moved)>();

            while (stack.TryPop(out var folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref scanner._currentPath, folder.Path);
                batch.Clear();

                // Old = -1: a folder that did not exist last time.
                var folderId = folder.Old >= 0 ? previous.FileId[folder.Old] : 0;
                if (folder.Old < 0 || folderId == 0 || changes.DirtyFolders.Contains(folderId))
                {
                    // Entries may have been added, removed or renamed: list the folder again.
                    FoldersRelisted++;
                    if (!scanner.ListLive(folder.Path, _state, cancellationToken))
                    {
                        continue;
                    }

                    var previousChildren = folder.Old >= 0 ? ChildrenById(folder.Old) : new Dictionary<long, int>();
                    for (var i = 0; i < _state.Count; i++)
                    {
                        ref var entry = ref _state.Entries[i];
                        var name = _state.Name(entry);
                        if (ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, _state.JoinPath(folder.Path, name)))
                        {
                            continue;
                        }

                        var meta = new FileMetadata(entry.FileId, entry.Attributes, entry.Size, entry.LastWriteTime);
                        meta = Refresh(meta, changes, hint);

                        var old = -1;
                        var moved = folder.Moved;
                        if (meta.IsDirectory && (meta.Attributes & NoDescend) == 0 && meta.FileId != 0)
                        {
                            if (previousChildren.TryGetValue(meta.FileId, out var sameParent))
                            {
                                old = sameParent;
                                moved |= !previousNames[previous.Name[old]].SequenceEqual(name);
                            }
                            else if (FolderById(meta.FileId) is var elsewhere and >= 0)
                            {
                                // Moved here from another folder.
                                old = elsewhere;
                                moved = true;
                            }
                        }

                        batch.Add((name.ToString(), -1, meta, old, moved));
                    }
                }
                else
                {
                    // Nothing was added, removed or renamed here: take the entries from the previous tree.
                    var end = folder.Old + previous.Descendants[folder.Old];
                    for (var c = folder.Old + 1; c <= end; c += previous.Descendants[c] + 1)
                    {
                        var name = previousNames[previous.Name[c]];
                        if (folder.Moved && ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, _state.JoinPath(folder.Path, name)))
                        {
                            continue;
                        }

                        var meta = new FileMetadata(previous.FileId[c], previous.Attributes[c], previous.Size[c], previous.LastWriteTime[c]);
                        meta = Refresh(meta, changes, hint);
                        batch.Add((null, previous.Name[c], meta, previous.IsDirectory(c) ? c : -1, folder.Moved));
                    }
                }

                lock (scanner._builderLock)
                {
                    foreach (var (listedName, nameId, meta, old, moved) in batch)
                    {
                        var name = listedName is not null ? listedName.AsSpan() : previousNames[nameId];
                        var index = scanner._builder.Add(folder.New, name, meta.Attributes, meta.Size, meta.LastWriteTime, meta.FileId);
                        if (meta.IsDirectory && IsLink(meta.Attributes))
                        {
                            scanner._links.Enqueue((index, string.Concat(folder.Path, "\\", name)));
                        }

                        if (!meta.IsDirectory || (meta.Attributes & NoDescend) != 0)
                        {
                            continue;
                        }

                        // New folders are walked too (not handed to the parallel listing) so that changed
                        // entries inside them are also read by id, exactly like everywhere else.
                        stack.Push((old, index, string.Concat(folder.Path, "\\", name), moved));
                    }
                }

                Interlocked.Add(ref scanner._entries, batch.Count);
            }
        }

        public void Dispose()
        {
            foreach (var handle in _volumeHints.Values)
            {
                handle.Dispose();
            }
        }

        /// <summary>Current metadata, read by file id, for anything the journal reports as changed.</summary>
        private static FileMetadata Refresh(FileMetadata meta, UsnChanges changes, SafeFileHandle? hint)
        {
            if (meta.FileId == 0 || hint is null ||
                !(changes.Changed.Contains(meta.FileId) || (meta.IsDirectory && changes.DirtyFolders.Contains(meta.FileId))))
            {
                return meta;
            }

            var current = FileIdentity.TryReadById(hint, meta.FileId);
            return current is { } fresh ? fresh with { FileId = meta.FileId } : meta;
        }

        /// <summary>Previous sub-folders of <paramref name="folder"/> by file id.</summary>
        private Dictionary<long, int> ChildrenById(int folder)
        {
            var result = new Dictionary<long, int>();
            var end = folder + previous.Descendants[folder];
            for (var c = folder + 1; c <= end; c += previous.Descendants[c] + 1)
            {
                if (previous.IsDirectory(c) && previous.FileId[c] != 0)
                {
                    result.TryAdd(previous.FileId[c], c);
                }
            }

            return result;
        }

        /// <summary>A previous folder anywhere in the tree with this file id (folders cannot have hard links), or -1.</summary>
        private int FolderById(long fileId)
        {
            if (_foldersById is null)
            {
                _foldersById = [];
                for (var i = 0; i < previous.Count; i++)
                {
                    if (previous.IsDirectory(i) && previous.FileId[i] != 0)
                    {
                        _foldersById.TryAdd(previous.FileId[i], i);
                    }
                }
            }

            return _foldersById.TryGetValue(fileId, out var index) ? index : -1;
        }

        private SafeFileHandle? VolumeHint(char drive)
        {
            drive = char.ToUpperInvariant(drive);
            if (!_volumeHints.TryGetValue(drive, out var handle))
            {
                handle = FileIdentity.OpenVolumeHint(drive);
                _volumeHints[drive] = handle;
            }

            return handle.IsInvalid ? null : handle;
        }
    }
}
