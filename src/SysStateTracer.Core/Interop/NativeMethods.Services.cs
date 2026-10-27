using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysStateTracer.Core.Interop;

/// <summary>Service Control Manager (plan §8.4).</summary>
internal static partial class NativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ScManagerEnumerateService = 0x0004;
    internal const uint ServiceQueryConfig = 0x0001;

    internal const uint ServiceWin32 = 0x00000030;
    internal const uint ServiceDriver = 0x0000000B;
    internal const uint ServiceUserServiceInstance = 0x00000080;
    internal const uint ServiceStateAll = 0x00000003;

    internal const int ScEnumProcessInfo = 0;
    internal const int ServiceConfigDelayedAutoStartInfo = 3;
    internal const int ServiceConfigTriggerInfo = 8;

    internal const int ErrorInsufficientBuffer = 122;

    [LibraryImport(Advapi32, EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport(Advapi32, EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeServiceHandle OpenService(SafeServiceHandle serviceManager, string serviceName, uint desiredAccess);

    [LibraryImport(Advapi32, EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumServicesStatusEx(
        SafeServiceHandle serviceManager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        byte* services,
        int bufferSize,
        out int bytesNeeded,
        out int servicesReturned,
        ref int resumeHandle,
        nint groupName);

    [LibraryImport(Advapi32, EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryServiceConfig(SafeServiceHandle service, byte* config, int bufferSize, out int bytesNeeded);

    [LibraryImport(Advapi32, EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryServiceConfig2(SafeServiceHandle service, int infoLevel, byte* buffer, int bufferSize, out int bytesNeeded);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);
}

/// <summary>Service or Service Control Manager handle closed with CloseServiceHandle.</summary>
internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}
