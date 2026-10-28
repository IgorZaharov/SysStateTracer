using SysStateTracer.Core.Export;

namespace SysStateTracer.Core.Tests.Export;

/// <summary>F7: the script written into .\Files.</summary>
public class RestoreScriptTests
{
    [Fact]
    public void Script_MapsTheLayoutBackAndSupportsWhatIf()
    {
        var text = RestoreScript.Text('d');

        Assert.Contains("[string] $SystemDrive = 'D:'", text, StringComparison.Ordinal);
        Assert.Contains("SupportsShouldProcess", text, StringComparison.Ordinal);
        Assert.Contains("'^_([A-Za-z])$'", text, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_UsesUtf8WithByteOrderMark()
    {
        using var dir = new TempDirectory();
        var path = RestoreScript.Write(dir.Path, 'C');

        Assert.Equal(Path.Combine(dir.Path, "Restore-Files.ps1"), path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
    }
}
