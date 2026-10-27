using SysStateTracer.Core.Interop;

namespace SysStateTracer.WindowsTests;

public class ProcessPrivilegesTests
{
    [Fact]
    public void IsElevated_CanBeQueried()
    {
        var ex = Record.Exception(() => { _ = ProcessPrivileges.IsElevated; });

        Assert.Null(ex);
    }

    [Fact]
    public void TryEnable_BackupPrivilege_SucceedsWhenElevated()
    {
        var enabled = ProcessPrivileges.TryEnable(ProcessPrivileges.Backup, out var error);

        if (ProcessPrivileges.IsElevated)
        {
            // Administrators hold SeBackupPrivilege; CI runners run elevated.
            Assert.True(enabled, $"error {error}");
            Assert.Equal(Win32Errors.Success, error);
        }
        else
        {
            Assert.False(enabled);
            Assert.Equal(Win32Errors.NotAllAssigned, error);
        }
    }

    [Fact]
    public void TryEnable_UnknownPrivilege_ReturnsFalseWithError()
    {
        var enabled = ProcessPrivileges.TryEnable("SeNoSuchPrivilegeForTests", out var error);

        Assert.False(enabled);
        Assert.Equal(Win32Errors.NoSuchPrivilege, error);
    }

    [Fact]
    public void Win32Errors_DescribeIncludesCode()
    {
        Assert.EndsWith("(5)", Win32Errors.Describe(Win32Errors.AccessDenied), StringComparison.Ordinal);
    }
}
