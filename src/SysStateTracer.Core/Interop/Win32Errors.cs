using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Interop;

/// <summary>Win32 error codes SysStateTracer reacts to, and helpers to describe or throw them.</summary>
public static class Win32Errors
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int InvalidHandle = 6;
    public const int SharingViolation = 32;
    public const int NotAllAssigned = 1300;
    public const int NoSuchPrivilege = 1313;
    public const int PrivilegeNotHeld = 1314;

    /// <summary>"Access is denied. (5)" — the system message plus the code.</summary>
    public static string Describe(int error) => $"{new Win32Exception(error).Message} ({error})";

    /// <summary>Exception for <paramref name="error"/> that names the failing operation.</summary>
    public static Win32Exception ToException(int error, string operation) =>
        new(error, $"{operation} failed: {Describe(error)}");

    /// <summary>Exception for the last error set by a <c>SetLastError = true</c> P/Invoke call.</summary>
    public static Win32Exception LastError(string operation) =>
        ToException(Marshal.GetLastPInvokeError(), operation);
}
