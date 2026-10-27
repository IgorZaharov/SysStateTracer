using BenchmarkDotNet.Running;

namespace SysStateTracer.Benchmarks;

internal static class Program
{
    // No arguments (double-clicked): the PC suite, results saved beside the exe.
    // "snapshot ..." takes one real snapshot and prints timings (plan Step 21).
    // Anything else goes to BenchmarkDotNet, e.g. --filter "*Naming*".
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return PcSuite.Run();
        }

        if (args[0].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotBenchmark.Run(args[1..]);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
