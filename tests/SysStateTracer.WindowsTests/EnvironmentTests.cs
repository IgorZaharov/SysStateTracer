using SysStateTracer.Core.Snapshots;

namespace SysStateTracer.WindowsTests;

public class EnvironmentTests
{
    [Fact]
    public void RunsOnWindows10OrLater()
    {
        Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10));
    }

    [Fact]
    public void RunsAs64BitProcess()
    {
        Assert.True(Environment.Is64BitProcess);
    }

    [Fact]
    public void DefaultName_EndsWithThisComputersName()
    {
        Assert.EndsWith("_" + Environment.MachineName, SnapshotNaming.DefaultName(), StringComparison.Ordinal);
    }
}
