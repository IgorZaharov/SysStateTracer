using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Scanning.Svc;

public sealed class ServicesScanOptions
{
    /// <summary>Win32 services (what services.msc shows).</summary>
    public bool IncludeServices { get; init; } = true;

    /// <summary>Kernel and file-system drivers (off by default, plan D9).</summary>
    public bool IncludeDrivers { get; init; }

    public IgnoreMatcher Ignore { get; init; } = IgnoreMatcher.Empty;
}

/// <summary>
/// Reads every service with its state, start type, delayed-start and trigger-start flags (plan §8.4).
/// Per-user service instances (e.g. "CDPUserSvc_1a2b3c") get a new suffix at each logon, so they are
/// stored as "CDPUserSvc_*" to avoid false add/delete pairs (plan 2.16).
/// </summary>
public sealed class ServicesScanner
{
    /// <summary>Start type stored when the service's configuration could not be read.</summary>
    public const uint UnknownStartType = uint.MaxValue;

    private const int EntrySize = 56; // ENUM_SERVICE_STATUS_PROCESSW on x64

    private readonly ServiceTableBuilder _builder;
    private readonly Lock _builderLock;
    private readonly SkippedCollector _skipped;
    private int _services;

    public ServicesScanner(ServiceTableBuilder builder, Lock builderLock, SkippedCollector skipped)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(skipped);
        _builder = builder;
        _builderLock = builderLock ?? throw new ArgumentNullException(nameof(builderLock));
        _skipped = skipped;
    }

    public int ServicesScanned => Volatile.Read(ref _services);

    public void Scan(ServicesScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var type = (options.IncludeServices ? NativeMethods.ServiceWin32 : 0) | (options.IncludeDrivers ? NativeMethods.ServiceDriver : 0);
        if (type == 0)
        {
            return;
        }

        using var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        if (manager.IsInvalid)
        {
            _skipped.Add("Services", Marshal.GetLastPInvokeError());
            return;
        }

        var listed = Enumerate(manager, type);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configBuffer = new byte[8 * 1024];

        foreach (var service in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (name, displayName) = NormalizeNames(service.Name, service.DisplayName, service.Type);
            if (options.Ignore.IsIgnoredEntry(IgnoreTarget.Service, name) ||
                options.Ignore.IsIgnoredEntry(IgnoreTarget.Service, service.Name))
            {
                continue;
            }

            var unique = name;
            for (var n = 2; !usedNames.Add(unique); n++)
            {
                unique = $"{name} #{n}";
            }

            var (startType, flags) = ReadConfiguration(manager, service.Name, ref configBuffer);
            lock (_builderLock)
            {
                _builder.Add(unique, displayName, service.Type, service.State, startType, flags);
            }

            Interlocked.Increment(ref _services);
        }
    }

    /// <summary>"CDPUserSvc_1a2b3c" → "CDPUserSvc_*" for per-user instances; other names unchanged.</summary>
    internal static (string Name, string DisplayName) NormalizeNames(string name, string displayName, uint type)
    {
        if ((type & NativeMethods.ServiceUserServiceInstance) == 0)
        {
            return (name, displayName);
        }

        var underscore = name.LastIndexOf('_');
        if (underscore <= 0)
        {
            return (name, displayName);
        }

        var suffix = name[underscore..];
        var display = displayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? displayName[..^suffix.Length]
            : displayName;
        return (name[..underscore] + "_*", display);
    }

    private unsafe List<(string Name, string DisplayName, uint Type, uint State)> Enumerate(SafeServiceHandle manager, uint type)
    {
        var result = new List<(string, string, uint, uint)>();
        var buffer = new byte[256 * 1024];
        var resume = 0;
        while (true)
        {
            bool ok;
            int needed;
            int returned;
            fixed (byte* p = buffer)
            {
                ok = NativeMethods.EnumServicesStatusEx(
                    manager, NativeMethods.ScEnumProcessInfo, type, NativeMethods.ServiceStateAll,
                    p, buffer.Length, out needed, out returned, ref resume, 0);

                for (var i = 0; i < returned; i++)
                {
                    var entry = p + (i * EntrySize);
                    var name = new string(*(char**)entry);
                    var display = new string(*(char**)(entry + 8));
                    var serviceType = *(uint*)(entry + 16);
                    var state = *(uint*)(entry + 20);
                    result.Add((name, display, serviceType, state));
                }
            }

            if (ok)
            {
                return result;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ErrorMoreData)
            {
                _skipped.Add("Services", error);
                return result;
            }

            if (returned == 0)
            {
                buffer = new byte[Math.Max(needed, buffer.Length * 2)];
            }
        }
    }

    private unsafe (uint StartType, ServiceFlags Flags) ReadConfiguration(SafeServiceHandle manager, string name, ref byte[] buffer)
    {
        using var service = NativeMethods.OpenService(manager, name, NativeMethods.ServiceQueryConfig);
        if (service.IsInvalid)
        {
            _skipped.Add($"Service {name}", Marshal.GetLastPInvokeError());
            return (UnknownStartType, ServiceFlags.None);
        }

        if (!Query((p, size) => (NativeMethods.QueryServiceConfig(service, p, size, out var needed), needed), ref buffer))
        {
            _skipped.Add($"Service {name}", Marshal.GetLastPInvokeError());
            return (UnknownStartType, ServiceFlags.None);
        }

        // QUERY_SERVICE_CONFIGW: DWORD dwServiceType, DWORD dwStartType, ...
        var startType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        var flags = ServiceFlags.None;

        if (Query((p, size) => (NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDelayedAutoStartInfo, p, size, out var needed), needed), ref buffer) &&
            BinaryPrimitives.ReadInt32LittleEndian(buffer) != 0)
        {
            flags |= ServiceFlags.DelayedAutoStart;
        }

        if (Query((p, size) => (NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigTriggerInfo, p, size, out var needed), needed), ref buffer) &&
            BinaryPrimitives.ReadUInt32LittleEndian(buffer) > 0)
        {
            flags |= ServiceFlags.TriggerStart;
        }

        return (startType, flags);
    }

    private unsafe delegate (bool Ok, int Needed) QueryCall(byte* buffer, int size);

    /// <summary>Calls a Query* function, growing the buffer once when it is too small.</summary>
    private static unsafe bool Query(QueryCall call, ref byte[] buffer)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            (bool Ok, int Needed) result;
            fixed (byte* p = buffer)
            {
                result = call(p, buffer.Length);
            }

            if (result.Ok)
            {
                return true;
            }

            if (Marshal.GetLastPInvokeError() != NativeMethods.ErrorInsufficientBuffer || result.Needed <= buffer.Length)
            {
                return false;
            }

            buffer = new byte[result.Needed];
        }

        return false;
    }
}
