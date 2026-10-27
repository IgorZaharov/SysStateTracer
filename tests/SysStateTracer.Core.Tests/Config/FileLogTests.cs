using SysStateTracer.Core.Config;

namespace SysStateTracer.Core.Tests.Config;

public class FileLogTests
{
    [Fact]
    public void Write_AppendsTimestampedLines()
    {
        using var dir = new TempDirectory();
        var log = new FileLog(dir.File("test.log"));

        log.Write("INF", "first");
        log.Write("ERR", "second", new InvalidOperationException("boom"));

        var text = File.ReadAllText(log.Path);
        Assert.Contains("[INF] first", text, StringComparison.Ordinal);
        Assert.Contains("[ERR] second", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RollsOverWhenTheFileIsFull()
    {
        using var dir = new TempDirectory();
        var log = new FileLog(dir.File("test.log"), maxBytes: 1024);
        var filler = new string('x', 300);

        for (var i = 0; i < 10; i++)
        {
            log.Write("INF", $"{i} {filler}");
        }

        Assert.True(File.Exists(log.PreviousPath));
        Assert.True(new FileInfo(log.Path).Length < 1024 + 400);
        Assert.Contains("9 xxx", File.ReadAllText(log.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ToUnwritablePath_DoesNotThrow()
    {
        using var dir = new TempDirectory();
        var log = new FileLog(Path.Combine(dir.Path, "missing-folder", "test.log"));

        var ex = Record.Exception(() => log.Write("INF", "lost"));

        Assert.Null(ex);
    }
}
