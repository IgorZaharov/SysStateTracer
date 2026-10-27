using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Interop;

/// <summary>
/// All Win32 declarations live in this partial class (one file per DLL area).
/// Scanner-specific calls (directory listing, USN journal, registry, services) are added
/// in the steps that use and test them.
/// </summary>
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";

    // ---- Handles ----

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>Pseudo-handle for the current process; never needs closing.</summary>
    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentProcess();

    // ---- Access tokens and privileges ----

    internal const uint TokenQuery = 0x0008;
    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint SePrivilegeEnabled = 0x0002;

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

    [LibraryImport(Advapi32, EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        SafeTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        nint previousState,
        nint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>TOKEN_PRIVILEGES with room for exactly one privilege.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }
}
