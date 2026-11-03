using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Core.Config;

namespace SysStateTracer;

/// <summary>
/// Gives memory back to Windows after a comparison window or New Snapshot closes (issue #9). What those windows
/// used is garbage once they close, but nothing makes the garbage collector run while the program sits idle, and
/// it keeps freed memory committed for reuse, so Task Manager kept showing hundreds of MB. One aggressive
/// collection, a moment after the last such window closed, frees it and returns it.
/// </summary>
internal static class MemoryRelease
{
    /// <summary>Wait after the last window closed, so closing several at once collects once.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    // A WinForms timer belongs to the thread that created it (the UI tests open windows on several threads).
    [ThreadStatic]
    private static System.Windows.Forms.Timer? _timer;

    /// <summary>Number of releases so far (tests).</summary>
    internal static int Count { get; private set; }

    /// <summary>Releases memory <see cref="Delay"/> from now; called again before then, it waits again.</summary>
    public static void Schedule()
    {
        if (_timer is null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = (int)Delay.TotalMilliseconds };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                ReleaseNow();
            };
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Collects all generations, compacting, and returns the freed memory to Windows.</summary>
    public static void ReleaseNow()
    {
        var watch = Stopwatch.StartNew();
        var committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
        // Objects with a finalizer (and everything they reference) survive the first collection until their
        // finalizer ran: run them, then collect again.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Count++;
        Log.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Memory released in {watch.ElapsedMilliseconds} ms: GC committed {committed / 1048576:N0} MB -> {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576:N0} MB, in use {GC.GetTotalMemory(false) / 1048576:N0} MB"));
    }
}
