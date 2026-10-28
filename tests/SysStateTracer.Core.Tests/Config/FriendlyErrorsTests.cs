using SysStateTracer.Core.Config;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Tests.Config;

/// <summary>Plan Step 53: errors are shown in plain words.</summary>
public class FriendlyErrorsTests
{
    [Fact]
    public void KnownErrors_GetPlainText()
    {
        Assert.StartsWith("There is not enough free space", FriendlyErrors.Describe(new IOException("x", unchecked((int)0x80070070))), StringComparison.Ordinal);
        Assert.StartsWith("There is not enough free space", FriendlyErrors.Describe(new IOException("x", unchecked((int)0x80070027))), StringComparison.Ordinal);
        Assert.StartsWith("The file is in use", FriendlyErrors.Describe(new IOException("x", unchecked((int)0x80070020))), StringComparison.Ordinal);
        Assert.Contains(@"C:\gone.stsnap", FriendlyErrors.Describe(new FileNotFoundException("x", @"C:\gone.stsnap")), StringComparison.Ordinal);
        Assert.StartsWith("Access is denied", FriendlyErrors.Describe(new UnauthorizedAccessException()), StringComparison.Ordinal);
        Assert.StartsWith("The snapshot was made by a newer", FriendlyErrors.Describe(new SnapshotFormatException("f", "The snapshot was made by a newer SysStateTracer (format 2). Please update SysStateTracer.")), StringComparison.Ordinal);
        Assert.Equal("anything else", FriendlyErrors.Describe(new InvalidOperationException("anything else")));
    }
}
