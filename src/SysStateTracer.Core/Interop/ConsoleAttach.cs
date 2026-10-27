using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Interop;

/// <summary>
/// SysStateTracer.exe is a window application, so Windows gives it no console. For command-line use it
/// attaches to the console of the program that started it (cmd, PowerShell), so output appears there (plan Step 39).
/// </summary>
public static partial class ConsoleAttach
{
    private const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>True when attached; false when there is no parent console (started from Explorer, or elevated from a normal console).</summary>
    public static bool ToParentConsole() => AttachConsole(AttachParentProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
