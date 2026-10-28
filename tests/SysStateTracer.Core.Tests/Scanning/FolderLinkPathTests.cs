using SysStateTracer.Core.Scanning.Fs;

namespace SysStateTracer.Core.Tests.Scanning;

/// <summary>Path rules for following folder links (GitHub issue #2).</summary>
public class FolderLinkPathTests
{
    [Theory]
    [InlineData("C:", @"\\?\C:\")]
    [InlineData(@"C:\Users\Alex\Desktop", @"\\?\C:\Users\Alex\Desktop")]
    [InlineData(@"\\fileserver\profiles$\alex\Desktop", @"\\?\UNC\fileserver\profiles$\alex\Desktop")]
    [InlineData(@"\\?\UNC\fileserver\share", @"\\?\UNC\fileserver\share")]
    [InlineData(@"\\?\Volume{0b1e2c3d-0000-0000-0000-100000000000}", @"\\?\Volume{0b1e2c3d-0000-0000-0000-100000000000}\")]
    public void OpenPath(string path, string expected) => Assert.Equal(expected, FileSystemScanner.OpenPath(path));

    [Theory]
    [InlineData(@"\\?\UNC\fileserver\share\alex\Desktop\", @"\\fileserver\share\alex\Desktop")]
    [InlineData(@"\\?\D:\Redirected\Desktop", @"D:\Redirected\Desktop")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"\\fileserver\share\", @"\\fileserver\share")]
    public void NormalizeTarget(string target, string expected) => Assert.Equal(expected, FileSystemScanner.NormalizeTarget(target));

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Roaming", @"C:\", true)]
    [InlineData(@"C:\Users", @"C:\Users", true)]
    [InlineData(@"C:\Users2", @"C:\Users", false)]
    [InlineData(@"D:\Data", @"C:\", false)]
    [InlineData(@"\\fileserver\share\alex", @"C:\", false)]
    [InlineData(@"\\fileserver\share\alex\Desktop", @"\\fileserver\share\alex", true)]
    public void IsSameOrInside(string path, string folder, bool expected) =>
        Assert.Equal(expected, FileSystemScanner.IsSameOrInside(path, folder));
}
