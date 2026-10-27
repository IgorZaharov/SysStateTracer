using SysStateTracer.Core.Snapshots;

namespace SysStateTracer.Core.Tests;

public class SnapshotNamingTests
{
    [Fact]
    public void DefaultName_FollowsDateTimeComputerPattern()
    {
        var name = SnapshotNaming.DefaultName(new DateTime(2026, 10, 27, 14, 5, 12), "WS-0142");

        Assert.Equal("20261027_14-05-12_WS-0142", name);
    }

    [Fact]
    public void DefaultName_PadsSingleDigitsWithZeros()
    {
        var name = SnapshotNaming.DefaultName(new DateTime(2026, 1, 2, 3, 4, 5), "PC");

        Assert.Equal("20260102_03-04-05_PC", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultName_RejectsEmptyComputerName(string computerName)
    {
        Assert.ThrowsAny<ArgumentException>(() => SnapshotNaming.DefaultName(DateTime.Now, computerName));
    }

    [Fact]
    public void DefaultName_IsAlwaysAValidName()
    {
        Assert.True(SnapshotNaming.IsValidName(SnapshotNaming.DefaultName(DateTime.Now, "WS-0142")));
    }

    [Theory]
    [InlineData("20261027_14-05-12_WS-0142")]
    [InlineData("Before 7-Zip install")]
    [InlineData("v1.2 baseline")]
    [InlineData("CONSOLE")]
    public void IsValidName_AcceptsNormalNames(string name)
    {
        Assert.True(SnapshotNaming.IsValidName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("trailing dot.")]
    [InlineData("a:b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b>")]
    [InlineData("a|b")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("com1.backup")]
    public void IsValidName_RejectsInvalidNames(string? name)
    {
        Assert.False(SnapshotNaming.IsValidName(name));
    }

    [Fact]
    public void IsValidName_RejectsOverlongNames()
    {
        Assert.True(SnapshotNaming.IsValidName(new string('a', SnapshotNaming.MaxNameLength)));
        Assert.False(SnapshotNaming.IsValidName(new string('a', SnapshotNaming.MaxNameLength + 1)));
    }
}
