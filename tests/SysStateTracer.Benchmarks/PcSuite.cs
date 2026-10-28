using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Core;

namespace SysStateTracer.Benchmarks;

/// <summary>
/// What runs when SysStateTracer.Bench.exe is double-clicked: drive C: only, each snapshot in its own process.
/// Results are shown and appended to SysStateTracer.Bench.results.txt beside the exe; the snapshot files
/// themselves go to %TEMP% and are deleted at the end. Only reads the system.
/// </summary>
internal static class PcSuite
{
    private static readonly (string Label, string[] Args)[] Runs =
    [
        ("files-mft", ["--only", "fs", "--roots", @"C:\", "--method", "mft"]),
        ("files-listing-1", ["--only", "fs", "--roots", @"C:\", "--method", "list"]),
        ("files-listing-2", ["--only", "fs", "--roots", @"C:\", "--method", "list"]),
        ("full-auto", ["--roots", @"C:\", "--cache", CacheToken]),
        ("consecutive-1", ["--roots", @"C:\", "--cache", CacheToken]),
        ("consecutive-2", ["--roots", @"C:\", "--cache", CacheToken]),

        // Loads and compares the two newest snapshots (the consecutive ones), as the comparison window does.
        ("compare", []),
    ];

    // Replaced by a cache file in the temporary snapshot folder.
    private const string CacheToken = "<cache>";

    public static int Run()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the program file.");
        var results = Path.Combine(Path.GetDirectoryName(exe)!, "SysStateTracer.Bench.results.txt");
        var output = Path.Combine(Path.GetTempPath(), "SysStateTracerBench-" + Guid.NewGuid().ToString("N")[..8]);

        using var log = new StreamWriter(results, append: true);
        void Write(string line)
        {
            Console.WriteLine(line);
            log.WriteLine(line);
            log.Flush();
        }

        Write(string.Create(CultureInfo.InvariantCulture, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.MachineName} {AppInfo.Version} {Environment.OSVersion}"));
        var failed = false;
        try
        {
            foreach (var (label, args) in Runs)
            {
                Write($"--- {label}");
                var start = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(label == "compare" ? "compare" : "snapshot");
                start.ArgumentList.Add(output);
                start.ArgumentList.Add("--label");
                start.ArgumentList.Add(label);
                foreach (var arg in args)
                {
                    start.ArgumentList.Add(arg == CacheToken ? Path.Combine(output, "SysStateTracer.cache") : arg);
                }

                using var child = Process.Start(start)!;
                child.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        lock (log)
                        {
                            Write("ERROR: " + e.Data);
                        }
                    }
                };
                child.BeginErrorReadLine();
                while (child.StandardOutput.ReadLine() is { } line)
                {
                    lock (log)
                    {
                        Write(line);
                    }
                }

                child.WaitForExit();
                if (child.ExitCode != 0)
                {
                    Write($"{label} exited with code {child.ExitCode}");
                    failed = true;
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(output, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write($"Could not delete {output}: {ex.Message}");
            }
        }

        Write($"Results saved to {results}");
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Done. Press Enter to close this window.");
            Console.ReadLine();
        }

        return failed ? 1 : 0;
    }
}
