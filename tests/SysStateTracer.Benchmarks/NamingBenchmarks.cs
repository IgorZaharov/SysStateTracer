using BenchmarkDotNet.Attributes;
using SysStateTracer.Core.Snapshots;

namespace SysStateTracer.Benchmarks;

/// <summary>Placeholder benchmark; scanner and comparer benchmarks arrive at Steps 21, 28 and 32.</summary>
[MemoryDiagnoser]
public class NamingBenchmarks
{
    private readonly DateTime _time = new(2026, 10, 27, 14, 5, 12);

    [Benchmark]
    public string DefaultName() => SnapshotNaming.DefaultName(_time, "WS-0142");

    [Benchmark]
    public bool IsValidName() => SnapshotNaming.IsValidName("20261027_14-05-12_WS-0142");
}
