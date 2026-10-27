using SysStateTracer.Core.Config;

namespace SysStateTracer.Core.Tests.Config;

public class AppPathsTests
{
    [Fact]
    public void Resolve_WritableExeFolder_KeepsEverythingBesideTheExe()
    {
        using var exe = new TempDirectory();
        using var fallback = new TempDirectory();

        var paths = AppPaths.Resolve(exe.Path, fallback.Path, _ => true);

        Assert.False(paths.UsesFallback);
        Assert.Equal(exe.Path, paths.DataDirectory);
        Assert.Equal(Path.Combine(exe.Path, "SysStateTracer.config"), paths.ConfigFile);
        Assert.Equal(Path.Combine(exe.Path, "SysStateTracer.cache"), paths.CacheFile);
        Assert.Equal(Path.Combine(exe.Path, "SysStateTracer.log"), paths.LogFile);
        Assert.Equal(Path.Combine(exe.Path, "Files"), paths.FilesDirectory);
        Assert.Equal(Path.Combine(exe.Path, "Backups"), paths.BackupsDirectory);
    }

    [Fact]
    public void Resolve_ReadOnlyExeFolder_FallsBackAndCreatesTheFolder()
    {
        using var exe = new TempDirectory();
        using var root = new TempDirectory();
        var fallback = Path.Combine(root.Path, "LocalAppData", "SysStateTracer");

        var paths = AppPaths.Resolve(exe.Path, fallback, _ => false);

        Assert.True(paths.UsesFallback);
        Assert.Equal(exe.Path, paths.ExeDirectory);
        Assert.Equal(fallback, paths.DataDirectory);
        Assert.True(Directory.Exists(fallback));
        Assert.Equal(Path.Combine(fallback, "SysStateTracer.config"), paths.ConfigFile);
    }

    [Fact]
    public void SnapshotDirectory_UsesPreferenceWhenSet()
    {
        using var exe = new TempDirectory();
        var paths = AppPaths.Resolve(exe.Path, exe.Path, _ => true);

        Assert.Equal(exe.Path, paths.SnapshotDirectory(new AppSettings()));
        Assert.Equal(@"D:\Snaps", paths.SnapshotDirectory(new AppSettings { SnapshotFolder = @"D:\Snaps" }));
    }

    [Fact]
    public void IsDirectoryWritable_TrueForTempFolder_AndLeavesNoProbeFile()
    {
        using var dir = new TempDirectory();

        Assert.True(AppPaths.IsDirectoryWritable(dir.Path));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void IsDirectoryWritable_FalseForMissingFolder()
    {
        using var dir = new TempDirectory();

        Assert.False(AppPaths.IsDirectoryWritable(Path.Combine(dir.Path, "does-not-exist")));
    }
}
