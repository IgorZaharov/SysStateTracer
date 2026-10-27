using Microsoft.Win32.SafeHandles;

namespace SysStateTracer.Core.Interop;

/// <summary>Access-token handle closed with CloseHandle.</summary>
internal sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTokenHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
