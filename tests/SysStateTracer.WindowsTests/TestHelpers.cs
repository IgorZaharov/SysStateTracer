using System.Diagnostics;
using Microsoft.Win32;

namespace SysStateTracer.WindowsTests;

/// <summary>A fresh folder under %TEMP%, deleted again on Dispose (read-only flags are cleared first).</summary>
internal sealed class TestFolder : IDisposable
{
    public TestFolder()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SysStateTracerWinTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        // %TEMP% may contain 8.3 short names (C:\Users\RUNNER~1\…); scans store the real names.
        Path = SysStateTracer.Core.Interop.NativeMethods.GetLongPath(path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string relative, int size = 0, FileAttributes extra = 0)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(full, data);
        if (extra != 0)
        {
            File.SetAttributes(full, File.GetAttributes(full) | extra);
        }

        return full;
    }

    public string CreateFolder(string relative) => Directory.CreateDirectory(Combine(relative)).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; %TEMP% is cleaned up eventually anyway.
        }
    }
}

/// <summary>A fresh key HKCU\Software\SysStateTracerTests\&lt;guid&gt;, deleted again on Dispose.</summary>
internal sealed class TestRegistryKey : IDisposable
{
    public const string Parent = @"Software\SysStateTracerTests";

    public TestRegistryKey()
    {
        RelativePath = Parent + "\\" + Guid.NewGuid().ToString("N");
        Key = Registry.CurrentUser.CreateSubKey(RelativePath, writable: true);
    }

    /// <summary>Path below HKCU.</summary>
    public string RelativePath { get; }

    /// <summary>e.g. HKCU\Software\SysStateTracerTests\0123…</summary>
    public string HkcuPath => @"HKCU\" + RelativePath;

    /// <summary>The same key as it is stored in snapshots: HKU\&lt;SID&gt;\Software\….</summary>
    public string StoredPath => @"HKU\" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "\\" + RelativePath;

    public RegistryKey Key { get; }

    public void Dispose()
    {
        Key.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RelativePath, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(Parent, writable: true);
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

internal static class Junction
{
    /// <summary>Creates a hard link with mklink /H.</summary>
    public static void CreateHardLink(string link, string target) => RunMklink("/H", link, target);

    /// <summary>Creates a directory junction with mklink /J (no administrator rights needed).</summary>
    public static void Create(string link, string target) => RunMklink("/J", link, target);

    private static void RunMklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink {kind} failed ({process.ExitCode}): {output}");
        }
    }
}
