using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysStateTracer.Core.Interop;

/// <summary>Registry reading (plan §8.3).</summary>
internal static partial class NativeMethods
{
    internal static readonly nint HkeyLocalMachine = unchecked((int)0x80000002);
    internal static readonly nint HkeyUsers = unchecked((int)0x80000003);

    internal const uint KeyQueryValue = 0x0001;
    internal const uint KeyEnumerateSubKeys = 0x0008;
    internal const uint KeyWow6464Key = 0x0100;

    /// <summary>Open a symbolic-link key itself instead of its target, so links are never followed.</summary>
    internal const uint RegOptionOpenLink = 0x0008;

    /// <summary>Open with backup semantics: access is checked against SeBackupPrivilege, not the key's permissions.</summary>
    internal const uint RegOptionBackupRestore = 0x0004;

    internal const int ErrorMoreData = 234;
    internal const int ErrorNoMoreItems = 259;

    [LibraryImport(Advapi32, EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(nint key, string? subKey, uint options, uint samDesired, out SafeRegKeyHandle result);

    [LibraryImport(Advapi32, EntryPoint = "RegOpenKeyExW")]
    internal static unsafe partial int RegOpenKeyEx(SafeRegKeyHandle key, char* subKey, uint options, uint samDesired, out SafeRegKeyHandle result);

    [LibraryImport(Advapi32, EntryPoint = "RegQueryInfoKeyW")]
    internal static partial int RegQueryInfoKey(
        SafeRegKeyHandle key,
        nint className,
        nint classNameLength,
        nint reserved,
        out uint subKeys,
        out uint maxSubKeyLength,
        nint maxClassLength,
        out uint values,
        out uint maxValueNameLength,
        out uint maxValueLength,
        nint securityDescriptorLength,
        out long lastWriteTime);

    [LibraryImport(Advapi32, EntryPoint = "RegEnumKeyExW")]
    internal static unsafe partial int RegEnumKeyEx(
        SafeRegKeyHandle key,
        uint index,
        char* name,
        ref uint nameLength,
        nint reserved,
        nint className,
        nint classNameLength,
        nint lastWriteTime);

    [LibraryImport(Advapi32, EntryPoint = "RegEnumValueW")]
    internal static unsafe partial int RegEnumValue(
        SafeRegKeyHandle key,
        uint index,
        char* valueName,
        ref uint valueNameLength,
        nint reserved,
        out uint type,
        byte* data,
        ref uint dataLength);

    internal const uint KeySetValue = 0x0002;

    [LibraryImport(Advapi32, EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegCreateKeyEx(
        nint key, string subKey, uint reserved, nint className, uint options, uint samDesired, nint securityAttributes,
        out SafeRegKeyHandle result, out uint disposition);

    [LibraryImport(Advapi32, EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int RegQueryValueEx(SafeRegKeyHandle key, string valueName, nint reserved, out uint type, byte* data, ref uint dataLength);

    [LibraryImport(Advapi32, EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int RegSetValueEx(SafeRegKeyHandle key, string valueName, uint reserved, uint type, byte* data, uint dataLength);

    [LibraryImport(Advapi32, EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegDeleteValue(SafeRegKeyHandle key, string valueName);

    [LibraryImport(Advapi32)]
    internal static partial int RegCloseKey(nint key);
}

/// <summary>Registry key handle closed with RegCloseKey.</summary>
internal sealed class SafeRegKeyHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeRegKeyHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.RegCloseKey(handle) == 0;
}
