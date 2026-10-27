using System.Collections.Concurrent;
using System.Security.Principal;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Scanning.Reg;

public sealed class RegistryScanOptions
{
    /// <summary>Keys to scan with everything below them: "HKLM", "HKU", or deeper such as "HKLM\SOFTWARE\Vendor".
    /// "HKCU" means the current user's key under HKU.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    public IgnoreMatcher Ignore { get; init; } = IgnoreMatcher.Empty;

    /// <summary>Keys read at the same time; 0 = automatic.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// The previous scan (from SysStateTracer.cache, with value data). Keys whose last-write time and value
    /// count are unchanged take their values from it instead of reading them (plan §8.6).
    /// </summary>
    internal RegTree? Previous { get; init; }

    internal NameTable? PreviousNames { get; init; }
}

/// <summary>
/// Full registry scan (plan §8.3). Worker threads read keys with the Win32 registry API into reused
/// buffers and add each key with its values to the shared builder in one short locked batch.
/// Large keys are split into separate work items so the threads stay busy.
/// Symbolic-link keys (e.g. CurrentControlSet) are recorded but never followed.
/// </summary>
public sealed class RegistryScanner
{
    private const uint Access = NativeMethods.KeyQueryValue | NativeMethods.KeyEnumerateSubKeys | NativeMethods.KeyWow6464Key;
    private const int MinSplitDepth = 2;
    private const int SplitThreshold = 256;
    private const int MaxKeyNameLength = 256;
    private const int MaxValueNameLength = 16384;

    private readonly RegTreeBuilder _builder;
    private readonly Lock _builderLock;
    private readonly SkippedCollector _skipped;
    private readonly Dictionary<string, int> _chainIndex = new(StringComparer.OrdinalIgnoreCase);

    private long _keys;
    private long _reusedKeys;
    private RegTree? _previous;
    private NameTable? _previousNames;
    private int _pending;
    private bool _backupPrivilege;
    private string? _currentPath;

    public RegistryScanner(RegTreeBuilder builder, Lock builderLock, SkippedCollector skipped)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(skipped);
        _builder = builder;
        _builderLock = builderLock ?? throw new ArgumentNullException(nameof(builderLock));
        _skipped = skipped;
    }

    public long KeysScanned => Interlocked.Read(ref _keys);

    public string? CurrentPath => Volatile.Read(ref _currentPath);

    /// <summary>Keys whose values were taken from the previous scan.</summary>
    public long KeysReused => Interlocked.Read(ref _reusedKeys);

    public void Scan(RegistryScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _backupPrivilege = ProcessPrivileges.TryEnable(ProcessPrivileges.Backup);
        _previous = options.Previous is { KeyCount: > 0 } && options.PreviousNames is not null ? options.Previous : null;
        _previousNames = _previous is null ? null : options.PreviousNames;
        using var jobs = new BlockingCollection<KeyJob>(new ConcurrentQueue<KeyJob>());

        foreach (var root in NormalizeRoots(options.Roots))
        {
            if (options.Ignore.IsIgnored(IgnoreTarget.Registry, root))
            {
                continue;
            }

            var (hive, relative) = Split(root);
            if (hive == 0)
            {
                _skipped.Add(root, Win32Errors.FileNotFound);
                continue;
            }

            var parent = EnsureChain(root, hive, relative);
            if (parent == int.MinValue)
            {
                continue;
            }

            var name = root[(root.LastIndexOf('\\') + 1)..];
            Interlocked.Increment(ref _pending);
            jobs.Add(
                new KeyJob(hive, relative, root, parent, name, relative.Length == 0 ? 0 : relative.Count(c => c == '\\') + 1, FindPrevious(root)),
                cancellationToken);
        }

        if (Volatile.Read(ref _pending) == 0)
        {
            return;
        }

        var workers = options.MaxDegreeOfParallelism > 0
            ? options.MaxDegreeOfParallelism
            : Math.Clamp(Environment.ProcessorCount, 2, 8);

        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
        {
            tasks[w] = Task.Factory.StartNew(
                () => Work(jobs, options.Ignore, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(tasks, CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            var cancelled = ex.InnerExceptions.OfType<OperationCanceledException>().FirstOrDefault();
            if (cancelled is not null)
            {
                throw new OperationCanceledException("The registry scan was stopped.", cancelled, cancellationToken);
            }

            throw ex.InnerExceptions[0];
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Maps HKEY_* names and HKCU, drops nested roots. Only HKLM and HKU are stored (plan 2.14).</summary>
    internal static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var normalized = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim().Replace('/', '\\').TrimEnd('\\');
            var slash = path.IndexOf('\\', StringComparison.Ordinal);
            var head = (slash < 0 ? path : path[..slash]).ToUpperInvariant();
            var rest = slash < 0 ? string.Empty : path[slash..];
            var mapped = head switch
            {
                "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
                "HKU" or "HKEY_USERS" => "HKU",
                "HKCU" or "HKEY_CURRENT_USER" => @"HKU\" + (WindowsIdentity.GetCurrent().User?.Value ?? ".DEFAULT"),
                _ => head,
            };

            normalized.Add(mapped + rest);
        }

        var ordered = normalized.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r.Length).ToList();
        var result = new List<string>();
        foreach (var root in ordered)
        {
            if (!result.Any(parent => root.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private static (nint Hive, string Relative) Split(string root)
    {
        var slash = root.IndexOf('\\', StringComparison.Ordinal);
        var head = slash < 0 ? root : root[..slash];
        var relative = slash < 0 ? string.Empty : root[(slash + 1)..];
        var hive = head switch
        {
            "HKLM" => NativeMethods.HkeyLocalMachine,
            "HKU" => NativeMethods.HkeyUsers,
            _ => (nint)0,
        };
        return (hive, relative);
    }

    /// <summary>
    /// Adds the hive root and the keys leading to a scan root (without their values) and returns the
    /// index of the scan root's parent (-1 when the scan root is the hive itself), or int.MinValue on failure.
    /// </summary>
    private int EnsureChain(string root, nint hive, string relative)
    {
        if (relative.Length == 0)
        {
            return -1;
        }

        var hiveName = root[..root.IndexOf('\\', StringComparison.Ordinal)];
        var segments = relative.Split('\\');
        lock (_builderLock)
        {
            if (!_chainIndex.TryGetValue(hiveName, out var parent))
            {
                var hiveTime = QueryLastWrite(hive, null, hiveName);
                if (hiveTime is null)
                {
                    return int.MinValue;
                }

                parent = _builder.AddRoot(hiveName, hiveTime.Value);
                _chainIndex[hiveName] = parent;
            }

            var display = hiveName;
            var path = string.Empty;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                display += "\\" + segments[i];
                path = path.Length == 0 ? segments[i] : path + "\\" + segments[i];
                if (!_chainIndex.TryGetValue(display, out var index))
                {
                    var time = QueryLastWrite(hive, path, display);
                    if (time is null)
                    {
                        return int.MinValue;
                    }

                    index = _builder.AddKey(parent, segments[i], time.Value);
                    _chainIndex[display] = index;
                }

                parent = index;
            }

            return parent;
        }
    }

    /// <summary>
    /// Opens a key for reading. Keys whose permissions shut out administrators are opened again with
    /// backup semantics (SeBackupPrivilege), which reads them regardless of their permissions.
    /// </summary>
    private int Open(nint hive, string? relative, uint options, out SafeRegKeyHandle key)
    {
        var error = NativeMethods.RegOpenKeyEx(hive, relative, options, Access, out key);
        if (error == Win32Errors.AccessDenied && _backupPrivilege)
        {
            key.Dispose();
            error = NativeMethods.RegOpenKeyEx(hive, relative, options | NativeMethods.RegOptionBackupRestore, Access, out key);
        }

        return error;
    }

    private unsafe int Open(SafeRegKeyHandle parent, char* name, uint options, out SafeRegKeyHandle key)
    {
        var error = NativeMethods.RegOpenKeyEx(parent, name, options, Access, out key);
        if (error == Win32Errors.AccessDenied && _backupPrivilege)
        {
            key.Dispose();
            error = NativeMethods.RegOpenKeyEx(parent, name, options | NativeMethods.RegOptionBackupRestore, Access, out key);
        }

        return error;
    }

    private long? QueryLastWrite(nint hive, string? relative, string display)
    {
        var error = Open(hive, relative, 0, out var key);
        using (key)
        {
            if (error == 0)
            {
                error = NativeMethods.RegQueryInfoKey(key, 0, 0, 0, out _, out _, 0, out _, out _, out _, 0, out var lastWrite);
                if (error == 0)
                {
                    return lastWrite;
                }
            }
        }

        _skipped.Add(display, error);
        return null;
    }

    private void Work(BlockingCollection<KeyJob> jobs, IgnoreMatcher ignore, CancellationToken cancellationToken)
    {
        var state = new WorkerState();
        foreach (var job in jobs.GetConsumingEnumerable(cancellationToken))
        {
            try
            {
                Volatile.Write(ref _currentPath, job.DisplayPath);
                var error = Open(job.Hive, job.RelativePath.Length == 0 ? null : job.RelativePath, NativeMethods.RegOptionOpenLink, out var key);
                using (key)
                {
                    if (error != 0)
                    {
                        _skipped.Add(job.DisplayPath, error);
                        continue;
                    }

                    state.Display.Reset(job.DisplayPath);
                    state.Relative.Reset(job.RelativePath);
                    ProcessKey(key, job.ParentIndex, job.Name, job.Hive, job.Depth, job.Previous, state, ignore, jobs, cancellationToken);
                }
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

    private unsafe void ProcessKey(
        SafeRegKeyHandle key,
        int parentIndex,
        ReadOnlySpan<char> name,
        nint hive,
        int depth,
        int previous,
        WorkerState state,
        IgnoreMatcher ignore,
        BlockingCollection<KeyJob> jobs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var error = NativeMethods.RegQueryInfoKey(
            key, 0, 0, 0, out var subKeys, out _, 0, out var valueCount, out var maxValueName, out var maxValueLength, 0, out var lastWrite);
        if (error != 0)
        {
            _skipped.Add(state.Display.ToString(), error);
            return;
        }

        // Windows updates a key's last-write time whenever one of its values is set or deleted.
        var reuse = previous >= 0 && _previous!.LastWriteTime[previous] == lastWrite && _previous.KeyValueCount[previous] == valueCount;
        state.Values.Clear();
        if (!reuse)
        {
            state.EnsureValueBuffers((int)maxValueName + 1, (int)maxValueLength);
            ReadValues(key, valueCount, state, ignore);
        }

        int index;
        lock (_builderLock)
        {
            index = parentIndex < 0 ? _builder.AddRoot(name, lastWrite) : _builder.AddKey(parentIndex, name, lastWrite);
            if (reuse)
            {
                CopyPreviousValues(previous, index);
            }
            else
            {
                state.Values.AddTo(_builder, index);
            }
        }

        if (reuse)
        {
            Interlocked.Increment(ref _reusedKeys);
        }

        Interlocked.Increment(ref _keys);
        if (subKeys == 0)
        {
            return;
        }

        var split = depth < MinSplitDepth || subKeys >= SplitThreshold;

        // Previous sub-keys in sorted order; both lists are sorted case-insensitively, so they are
        // matched by walking them side by side. A sub-key without a match is simply read in full.
        var previousChild = previous >= 0 ? previous + 1 : -1;
        var previousEnd = previous >= 0 ? previous + _previous!.Descendants[previous] : -1;
        fixed (char* keyName = state.KeyName)
        {
            for (uint i = 0; ; i++)
            {
                var nameLength = (uint)MaxKeyNameLength;
                error = NativeMethods.RegEnumKeyEx(key, i, keyName, ref nameLength, 0, 0, 0, 0);
                if (error == NativeMethods.ErrorNoMoreItems)
                {
                    break;
                }

                if (error != 0)
                {
                    _skipped.Add(state.Display.ToString(), error);
                    break;
                }

                var childName = new ReadOnlySpan<char>(keyName, (int)nameLength);
                var childPrevious = -1;
                while (previousChild >= 0 && previousChild <= previousEnd)
                {
                    var order = childName.CompareTo(_previousNames![_previous!.Name[previousChild]], StringComparison.OrdinalIgnoreCase);
                    if (order < 0)
                    {
                        break;
                    }

                    var candidate = previousChild;
                    previousChild += _previous.Descendants[previousChild] + 1;
                    if (order == 0)
                    {
                        childPrevious = candidate;
                        break;
                    }
                }

                state.Display.Push(childName);
                state.Relative.Push(childName);
                try
                {
                    if (ignore.IsIgnoredEntry(IgnoreTarget.Registry, state.Display.Span))
                    {
                        continue;
                    }

                    if (split)
                    {
                        Interlocked.Increment(ref _pending);
                        jobs.Add(
                            new KeyJob(hive, state.Relative.ToString(), state.Display.ToString(), index, childName.ToString(), depth + 1, childPrevious),
                            cancellationToken);
                        continue;
                    }

                    error = Open(key, keyName, NativeMethods.RegOptionOpenLink, out var child);
                    using (child)
                    {
                        if (error != 0)
                        {
                            _skipped.Add(state.Display.ToString(), error);
                            continue;
                        }

                        ProcessKey(child, index, childName, hive, depth + 1, childPrevious, state, ignore, jobs, cancellationToken);
                    }
                }
                finally
                {
                    state.Display.Pop();
                    state.Relative.Pop();
                }
            }
        }
    }

    /// <summary>Reads all values of a key; a Global Ignore rule naming "key\value" leaves that value out.</summary>
    private unsafe void ReadValues(SafeRegKeyHandle key, uint valueCount, WorkerState state, IgnoreMatcher ignore)
    {
        var checkIgnore = ignore.RuleCount > 0;
        for (uint v = 0; v < valueCount; v++)
        {
            var retries = 0;
            while (true)
            {
                var nameLength = (uint)state.ValueName.Length;
                var dataLength = (uint)state.Data.Length;
                uint type;
                int error;
                fixed (char* valueName = state.ValueName)
                fixed (byte* data = state.Data)
                {
                    error = NativeMethods.RegEnumValue(key, v, valueName, ref nameLength, 0, out type, data, ref dataLength);
                }

                if (error == NativeMethods.ErrorMoreData && retries++ < 3)
                {
                    // The value grew while being read; make room and read it again.
                    state.EnsureValueBuffers(MaxValueNameLength + 1, (int)Math.Max(dataLength, (uint)state.Data.Length * 2));
                    continue;
                }

                if (error == NativeMethods.ErrorNoMoreItems)
                {
                    return;
                }

                if (error != 0)
                {
                    _skipped.Add($"{state.Display} (value {v})", error);
                    break;
                }

                var storedName = state.ValueName.AsSpan(0, (int)nameLength);
                if (checkIgnore)
                {
                    state.Display.Push(storedName);
                    var ignored = ignore.IsIgnoredEntry(IgnoreTarget.Registry, state.Display.Span);
                    state.Display.Pop();
                    if (ignored)
                    {
                        break;
                    }
                }

                state.Values.Add(storedName, type, state.Data.AsSpan(0, (int)dataLength));
                break;
            }
        }
    }

    private readonly record struct KeyJob(nint Hive, string RelativePath, string DisplayPath, int ParentIndex, string Name, int Depth, int Previous);

    /// <summary>Adds the values of previous key <paramref name="previous"/> to new key <paramref name="key"/>. Caller holds the lock.</summary>
    private void CopyPreviousValues(int previous, int key)
    {
        var tree = _previous!;
        var first = tree.FirstValue[previous];
        var end = first + tree.KeyValueCount[previous];
        for (var v = first; v < end; v++)
        {
            _builder.AddKnownValue(
                key,
                _previousNames![tree.ValueName[v]],
                tree.ValueDataType[v],
                tree.ValueDataHash[v],
                tree.ValueDataLength[v],
                tree.GetValueData(v),
                tree.IsValueDataStored(v));
        }
    }

    /// <summary>The previous scan's key at <paramref name="path"/> (e.g. "HKLM\SOFTWARE"), or -1.</summary>
    private int FindPrevious(string path)
    {
        if (_previous is null)
        {
            return -1;
        }

        var tree = _previous;
        var names = _previousNames!;
        var segments = path.Split('\\');
        var current = -1;
        foreach (var segment in segments)
        {
            var start = current < 0 ? 0 : current + 1;
            var end = current < 0 ? tree.KeyCount - 1 : current + tree.Descendants[current];
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

    /// <summary>A path built segment by segment in a reused buffer.</summary>
    private sealed class PathBuffer
    {
        private readonly Stack<int> _lengths = new();
        private char[] _chars = new char[512];
        private int _length;

        public ReadOnlySpan<char> Span => _chars.AsSpan(0, _length);

        public void Reset(string path)
        {
            _lengths.Clear();
            _length = 0;
            Append(path);
        }

        public void Push(ReadOnlySpan<char> segment)
        {
            _lengths.Push(_length);
            if (_length > 0)
            {
                Append("\\");
            }

            Append(segment);
        }

        public void Pop() => _length = _lengths.Pop();

        public override string ToString() => new(Span);

        private void Append(ReadOnlySpan<char> text)
        {
            if (_length + text.Length > _chars.Length)
            {
                Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _length + text.Length));
            }

            text.CopyTo(_chars.AsSpan(_length));
            _length += text.Length;
        }
    }

    /// <summary>The values of one key, held until they are added to the builder.</summary>
    private sealed class ValueBatch
    {
        private readonly List<(int NameStart, int NameLength, uint Type, int DataStart, int DataLength)> _values = [];
        private char[] _names = new char[1024];
        private int _nameLength;
        private byte[] _data = new byte[16 * 1024];
        private int _dataLength;

        public void Clear()
        {
            _values.Clear();
            _nameLength = 0;
            _dataLength = 0;
        }

        public void Add(ReadOnlySpan<char> name, uint type, ReadOnlySpan<byte> data)
        {
            if (_nameLength + name.Length > _names.Length)
            {
                Array.Resize(ref _names, Math.Max(_names.Length * 2, _nameLength + name.Length));
            }

            if (_dataLength + data.Length > _data.Length)
            {
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _dataLength + data.Length));
            }

            name.CopyTo(_names.AsSpan(_nameLength));
            data.CopyTo(_data.AsSpan(_dataLength));
            _values.Add((_nameLength, name.Length, type, _dataLength, data.Length));
            _nameLength += name.Length;
            _dataLength += data.Length;
        }

        public void AddTo(RegTreeBuilder builder, int key)
        {
            foreach (var (nameStart, nameLength, type, dataStart, dataLength) in _values)
            {
                builder.AddValue(key, _names.AsSpan(nameStart, nameLength), type, _data.AsSpan(dataStart, dataLength));
            }
        }
    }

    private sealed class WorkerState
    {
        public char[] KeyName { get; } = new char[MaxKeyNameLength + 1];

        public char[] ValueName { get; private set; } = new char[256];

        public byte[] Data { get; private set; } = new byte[4096];

        public PathBuffer Display { get; } = new();

        public PathBuffer Relative { get; } = new();

        public ValueBatch Values { get; } = new();

        public void EnsureValueBuffers(int nameChars, int dataBytes)
        {
            if (ValueName.Length < nameChars)
            {
                ValueName = new char[Math.Min(Math.Max(nameChars, ValueName.Length * 2), MaxValueNameLength + 1)];
            }

            if (Data.Length < dataBytes)
            {
                Data = new byte[Math.Max(dataBytes, Data.Length * 2)];
            }
        }
    }
}
