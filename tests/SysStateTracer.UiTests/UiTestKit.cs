using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using SysStateTracer.Theming;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SysStateTracer.UiTests;

/// <summary>Runs window code on an STA thread, pumps messages, and saves screenshots.</summary>
internal static class Ui
{
    static Ui()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>Folder for screenshots (CI sets it), or null to skip them.</summary>
    public static string? ScreenshotFolder => Environment.GetEnvironmentVariable("SYSSTATETRACER_SCREENSHOTS") is { Length: > 0 } folder
        ? Directory.CreateDirectory(folder).FullName
        : null;

    /// <summary>Runs <paramref name="action"/> on a new STA thread and rethrows its exception.</summary>
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>Processes window messages for a while (layout, paint, async continuations).</summary>
    public static void Pump(int milliseconds = 150)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        while (watch.ElapsedMilliseconds < milliseconds);
    }

    /// <summary>Pumps messages until <paramref name="condition"/> holds; false on timeout.</summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                return false;
            }

            Application.DoEvents();
            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>Shows the window at a fixed place, lets it paint, and returns it.</summary>
    public static T Show<T>(T form)
        where T : Form
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(10, 10);
        form.Show();
        Pump(250);
        return form;
    }

    /// <summary>Saves the window, title bar included, when screenshots are enabled.</summary>
    public static void Screenshot(Form form, string name)
    {
        if (ScreenshotFolder is not { } folder)
        {
            return;
        }

        Pump(200);
        var size = form.Size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        bool printed;
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                printed = NativeUi.PrintWindow(form.Handle, hdc);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (!printed)
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        }

        var theme = ThemeManager.Current.IsDark ? "dark" : "light";
        bitmap.Save(Path.Combine(folder, $"{name}-{theme}.png"), ImageFormat.Png);
    }
}

/// <summary>A program folder and a snapshot folder with hand-made snapshots.</summary>
internal sealed class TestApp : IDisposable
{
    public TestApp()
    {
        Root = Path.Combine(Path.GetTempPath(), "SysStateTracerUiTests", Guid.NewGuid().ToString("N"));
        DataFolder = Directory.CreateDirectory(Path.Combine(Root, "app")).FullName;
        SnapshotFolder = Directory.CreateDirectory(Path.Combine(Root, "snapshots")).FullName;
        File.WriteAllText(Path.Combine(DataFolder, AppPaths.LocalIgnoreFileName), "# Test list" + Environment.NewLine + @"FS:C:\Windows\Temp" + Environment.NewLine + @"REG:HKLM\SAM" + Environment.NewLine + @"REG:HKLM\SECURITY" + Environment.NewLine + "SVC:edgeupdate" + Environment.NewLine);
        Paths = AppPaths.Resolve(DataFolder, Path.Combine(Root, "fallback"), _ => true);
        Settings = new AppSettings { SnapshotFolder = SnapshotFolder, IgnoreListSource = Core.Ignore.IgnoreListSource.Local };
    }

    public string Root { get; }

    public string DataFolder { get; }

    public string SnapshotFolder { get; }

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    /// <summary>Writes a small snapshot taken "<paramref name="hoursAgo"/>" hours ago.</summary>
    public string AddSnapshot(string name, double hoursAgo, string computer = "OFFICE-PC", int extraFiles = 0, bool withSkipped = false)
    {
        var names = new NameTable();
        var fs = new FsTreeBuilder(names);
        var time = DateTime.UtcNow.AddDays(-30).ToFileTimeUtc();
        var c = fs.AddRoot("C:", FsTree.DirectoryAttribute, time);
        var programs = fs.Add(c, "Program Files", FsTree.DirectoryAttribute, 0, time);
        var app = fs.Add(programs, "Contoso", FsTree.DirectoryAttribute, 0, time);
        fs.Add(app, "contoso.exe", 0x20, 2_400_000, time);
        for (var i = 0; i < extraFiles; i++)
        {
            fs.Add(app, $"plugin{i:D2}.dll", 0x20, 40_000 + i, time);
        }

        var reg = new RegTreeBuilder(names);
        var hklm = reg.AddRoot("HKLM", time);
        var software = reg.AddKey(hklm, "SOFTWARE", time);
        var vendor = reg.AddKey(software, "Contoso", time);
        reg.AddValue(vendor, "Version", 1, System.Text.Encoding.Unicode.GetBytes("1.0\0"));
        var svc = new ServiceTableBuilder(names);
        svc.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None);

        var started = DateTimeOffset.Now.AddHours(-hoursAgo);
        var meta = new SnapshotMeta
        {
            Name = name,
            StartedAt = started,
            CompletedAt = started.AddSeconds(9.4),
            ComputerName = computer,
            UserName = "alex",
            OsEdition = "Windows 11 Pro",
            OsVersion = "10.0.26100.6584",
            AppVersion = "0.1.0+1a2b3c4",
            Mode = hoursAgo > 100 ? SnapshotMode.Full : SnapshotMode.Incremental,
            ModeReason = hoursAgo > 100 ? "no previous snapshot" : null,
            Scope = [@"C:\", "HKLM", "HKU", "Services"],
            FileSystemMethods = ["C: master file table"],
            IgnoreRuleCount = 94,
            Timings = new SnapshotTimings { FileSystemMs = 2_050, RegistryMs = 6_980, ServicesMs = 140, WriteMs = 410, TotalMs = 9_420 },
        };
        IReadOnlyList<SkippedItem>? skipped = withSkipped
            ? [new(@"C:\System Volume Information", 5), new(@"HKLM\SOFTWARE\Locked\Key", 5), new(@"C:\pagefile.sys", 32)]
            : null;
        var snapshot = new Snapshot(meta, names, fs.Build(), reg.Build(), svc.Build(), skipped);
        names.Freeze();
        var path = Path.Combine(SnapshotFolder, name + ".stsnap");
        SnapshotWriter.Write(path, snapshot);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
