using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Scanning.Fs;

namespace SysStateTracer.WindowsTests;

public class VolumeDiscoveryTests
{
    private static readonly string SystemRoot = Path.GetPathRoot(Environment.SystemDirectory)!.ToUpperInvariant();

    [Fact]
    public void GetFixedVolumes_IncludesSystemDrive()
    {
        var volumes = VolumeDiscovery.GetFixedVolumes();

        var system = Assert.Single(volumes, v => v.RootPath == SystemRoot);
        Assert.Equal(SystemRoot[..2], system.Name);
        Assert.Equal(DriveType.Fixed, system.DriveType);
        Assert.Equal("NTFS", system.FileSystem);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
        Console.WriteLine(string.Join(" | ", volumes.Select(v =>
            $"{v.Name} {v.FileSystem} ssd={v.IsSsd?.ToString() ?? "?"} journal={v.HasChangeJournal} entries≈{v.EstimatedEntries?.ToString("N0") ?? "?"}")));
    }

    [Fact]
    public void TryQuery_AcceptsAnyPathOnTheDrive()
    {
        var info = VolumeDiscovery.TryQuery(Environment.SystemDirectory.ToLowerInvariant());

        Assert.NotNull(info);
        Assert.Equal(SystemRoot, info.RootPath);
        Assert.NotEqual(0u, info.SerialNumber);
    }

    [Fact]
    public void TryQuery_ReadsJournalAndRecordCountWhenElevated()
    {
        var info = VolumeDiscovery.TryQuery(SystemRoot)!;

        if (ProcessPrivileges.IsElevated)
        {
            Assert.True(info.HasChangeJournal, "the system drive keeps a USN journal");
            Assert.True(info.EstimatedEntries > 10_000, $"{info.EstimatedEntries} MFT records");
        }
        else
        {
            Assert.Null(info.ChangeJournalId);
        }
    }

    [Fact]
    public void TryQuery_UncPathReturnsNull()
    {
        Assert.Null(VolumeDiscovery.TryQuery(@"\\server\share\folder"));
    }
}
