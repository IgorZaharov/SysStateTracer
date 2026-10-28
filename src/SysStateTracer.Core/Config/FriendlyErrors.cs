using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Config;

/// <summary>Plain-language text for the errors a user can run into (plan Step 53).</summary>
public static class FriendlyErrors
{
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);
    private const int ErrorNotReady = unchecked((int)0x80070015);

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SnapshotFormatException => exception.Message,
            FileNotFoundException notFound => $"The file is no longer there{Where(notFound.FileName)}. It may have been deleted, renamed or moved.",
            DirectoryNotFoundException => "The folder is no longer there. It may have been deleted, renamed or moved, or a drive was disconnected.",
            DriveNotFoundException => "The drive is not available.",
            UnauthorizedAccessException => "Access is denied: Windows does not allow SysStateTracer to read or write this file or folder. " +
                "Check that the folder is not read-only and that SysStateTracer runs as administrator.",
            PathTooLongException => "The path is too long.",
            IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => "There is not enough free space on the disk. Free some space or choose another snapshot folder in Preferences.",
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation => "The file is in use by another program. Close it there and try again.",
            IOException io when io.HResult is ErrorNotReady => "The drive is not ready.",
            IOException io when io.HResult is ErrorPathNotFound => "The folder is no longer there.",
            OperationCanceledException => "Cancelled.",
            _ => exception.Message,
        };
    }

    private static string Where(string? path) => string.IsNullOrEmpty(path) ? string.Empty : $": {path}";
}
