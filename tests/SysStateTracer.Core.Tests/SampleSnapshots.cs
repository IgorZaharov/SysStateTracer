using System.Text;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Tests;

/// <summary>Small, hand-built snapshots covering every kind of data.</summary>
internal static class SampleSnapshots
{
    public const uint Dir = FsTree.DirectoryAttribute;
    public const uint Archive = 0x20;

    public static long Time(int day, int hour = 12) => new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    public static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    public static Snapshot Create()
    {
        var names = new NameTable();

        var fs = new FsTreeBuilder(names);
        var d = fs.AddRoot("D:", Dir, Time(1));
        fs.Add(d, "Data", Dir, 0, Time(2));
        var c = fs.AddRoot("C:", Dir, Time(1));
        var windows = fs.Add(c, "Windows", Dir, 0, Time(5));
        var system32 = fs.Add(windows, "System32", Dir, 0, Time(5));
        fs.Add(system32, "kernel32.dll", Archive, 812_000, Time(3));
        fs.Add(windows, "notepad.exe", Archive, 360_448, Time(3));
        fs.Add(c, "pagefile.sys", 0x26, 8L << 30, Time(27, 9));
        fs.Add(c, "ProgramData", Dir, 0, Time(20));
        fs.Add(c, "boot", Dir, 0, Time(1));
        fs.Add(c, "apps", Dir, 0, Time(1));

        var reg = new RegTreeBuilder(names);
        var hku = reg.AddRoot("HKU", Time(1));
        var user = reg.AddKey(hku, "S-1-5-21-1000", Time(1));
        var env = reg.AddKey(user, "Environment", Time(26));
        reg.AddValue(env, "TOOLS_HOME", 1, Sz(@"C:\Tools"));
        var hklm = reg.AddRoot("HKLM", Time(1));
        var software = reg.AddKey(hklm, "SOFTWARE", Time(20));
        var app = reg.AddKey(software, "7-Zip", Time(27));
        reg.AddValue(app, "Path", 1, Sz(@"C:\Program Files\7-Zip"));
        reg.AddValue(app, string.Empty, 1, Sz("default"));
        reg.AddValue(app, "Version", 4, BitConverter.GetBytes(23));
        reg.AddValue(app, "Path64", 1, Sz(@"C:\Program Files\7-Zip"));
        reg.AddValue(app, "Big", 3, new byte[RegTreeBuilder.MaxStoredValueSize + 1]);
        reg.AddKey(software, "Classes", Time(20));

        var services = new ServiceTableBuilder(names);
        services.Add("Spooler", "Print Spooler", 0x10, 4, 2, ServiceFlags.None);
        services.Add("BITS", "Background Intelligent Transfer Service", 0x20, 1, 2, ServiceFlags.DelayedAutoStart | ServiceFlags.TriggerStart);
        services.Add("edgeupdate", "Microsoft Edge Update Service", 0x10, 1, 3, ServiceFlags.TriggerStart);

        var meta = new SnapshotMeta
        {
            Name = "20261027_14-05-12_WS-0142",
            StartedAt = new DateTimeOffset(2026, 10, 27, 14, 5, 12, TimeSpan.FromHours(2)),
            CompletedAt = new DateTimeOffset(2026, 10, 27, 14, 5, 21, TimeSpan.FromHours(2)),
            ComputerName = "WS-0142",
            UserName = "admin",
            OsEdition = "Windows 11 Pro 25H2",
            OsVersion = "10.0.26200.6899",
            AppVersion = "0.1.0+test",
            Mode = SnapshotMode.Incremental,
            ModeReason = "D: has no change journal",
            Scope = [@"C:\", @"D:\", "HKLM", "HKU", "Services"],
            IgnoreRuleCount = 12,
            Timings = new SnapshotTimings { FileSystemMs = 2100, RegistryMs = 5300, ServicesMs = 400, TotalMs = 8700 },
        };

        var snapshot = new Snapshot(
            meta,
            names,
            fs.Build(),
            reg.Build(),
            services.Build(),
            [new SkippedItem(@"C:\System Volume Information", 5), new SkippedItem(@"HKLM\SECURITY", 5)]);
        names.Freeze();
        return snapshot;
    }
}
