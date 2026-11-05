using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysStateTracer.Core.Interop;

/// <summary>Reading security descriptors of keys, files and folders (owner and DACL only).</summary>
internal static partial class NativeMethods
{
    /// <summary>Lets a handle's security descriptor be read (owner and DACL).</summary>
    internal const uint ReadControl = 0x00020000;

    internal const uint OwnerSecurityInformation = 0x1;
    internal const uint DaclSecurityInformation = 0x4;

    [LibraryImport(Advapi32)]
    internal static unsafe partial int RegGetKeySecurity(SafeRegKeyHandle key, uint securityInformation, byte* securityDescriptor, ref uint length);

    [LibraryImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetKernelObjectSecurity(SafeFileHandle handle, uint requestedInformation, byte* securityDescriptor, uint length, out uint lengthNeeded);

    [LibraryImport(Advapi32)]
    internal static unsafe partial uint GetSecurityDescriptorLength(byte* securityDescriptor);
}
