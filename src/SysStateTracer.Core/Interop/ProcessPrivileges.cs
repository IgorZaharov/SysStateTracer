using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Interop;

/// <summary>Elevation check and privilege enabling for the current process.</summary>
public static class ProcessPrivileges
{
    /// <summary>Lets an administrator list folders whose ACL denies Administrators (metadata only; plan §8.2).</summary>
    public const string Backup = "SeBackupPrivilege";

    /// <summary>True when the process runs elevated (UAC admin token).</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>
    /// Enables <paramref name="privilegeName"/> in the process token.
    /// Returns false (never throws) when the account does not hold the privilege,
    /// the name is unknown, or the token cannot be opened.
    /// </summary>
    public static bool TryEnable(string privilegeName) => TryEnable(privilegeName, out _);

    /// <inheritdoc cref="TryEnable(string)"/>
    /// <param name="privilegeName">Privilege constant name, e.g. <see cref="Backup"/>.</param>
    /// <param name="error">Win32 error code when the result is false, otherwise 0.</param>
    public static bool TryEnable(string privilegeName, out int error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        var opened = NativeMethods.OpenProcessToken(
            NativeMethods.GetCurrentProcess(),
            NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
            out var token);

        using (token)
        {
            if (!opened || !NativeMethods.LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new NativeMethods.LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled,
                },
            };

            if (!NativeMethods.AdjustTokenPrivileges(
                    token, false, ref privileges, Marshal.SizeOf<NativeMethods.TokenPrivileges>(), 0, 0))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            // AdjustTokenPrivileges "succeeds" even when the account lacks the privilege;
            // that case is reported only through ERROR_NOT_ALL_ASSIGNED.
            error = Marshal.GetLastPInvokeError();
            return error == Win32Errors.Success;
        }
    }
}
