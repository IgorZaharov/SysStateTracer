using Microsoft.Win32;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Svc;

namespace SysStateTracer.WindowsTests;

public class ServicesScannerTests
{
    private const uint Win32Types = 0x30;
    private const uint UserServiceInstance = 0x80;
    private const uint Running = 4;
    private const uint AutoStart = 2;

    [Fact]
    public void Scan_ReadsCoreServices()
    {
        var scan = ScanResult.Run(new ServicesScanOptions());

        Assert.True(scan.Table.Count > 100, $"{scan.Table.Count} services");
        foreach (var name in new[] { "EventLog", "RpcSs", "Winmgmt" })
        {
            var i = scan.Rows[name];
            Assert.Equal(Running, scan.Table.State[i]);
            Assert.Equal(AutoStart, scan.Table.StartType[i]);
            Assert.NotEqual(0u, scan.Table.ServiceType[i] & Win32Types);
            Assert.False(string.IsNullOrEmpty(scan.Names.GetString(scan.Table.DisplayName[i])));
        }
    }

    [Fact]
    public void Scan_StartTypesMatchRegistry()
    {
        var scan = ScanResult.Run(new ServicesScanOptions());
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")!;

        var compared = 0;
        foreach (var (name, i) in scan.Rows)
        {
            if (name.Contains('*', StringComparison.Ordinal) || name.Contains(" #", StringComparison.Ordinal))
            {
                continue;
            }

            using var key = services.OpenSubKey(name);
            if (key?.GetValue("Start") is not int start)
            {
                continue;
            }

            Assert.True(start == scan.Table.StartType[i], $"{name}: registry {start}, scanned {scan.Table.StartType[i]}");
            if (start == AutoStart)
            {
                var delayed = key.GetValue("DelayedAutostart") is 1;
                var flagged = scan.Table.Flags[i].HasFlag(ServiceFlags.DelayedAutoStart);
                Assert.True(delayed == flagged, $"{name}: registry delayed={delayed}, scanned {flagged}");
            }

            compared++;
        }

        Assert.True(compared > 100, $"only {compared} compared");
    }

    [Fact]
    public void Scan_StoresPerUserServicesUnderStableNames()
    {
        var scan = ScanResult.Run(new ServicesScanOptions());

        foreach (var (name, i) in scan.Rows)
        {
            if ((scan.Table.ServiceType[i] & UserServiceInstance) != 0)
            {
                Assert.EndsWith("_*", name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Scan_DriversOnlyWhenAsked()
    {
        var services = ScanResult.Run(new ServicesScanOptions());
        var drivers = ScanResult.Run(new ServicesScanOptions { IncludeServices = false, IncludeDrivers = true });

        Assert.DoesNotContain("Tcpip", services.Rows.Keys);
        Assert.Contains("Tcpip", drivers.Rows.Keys);
        Assert.DoesNotContain("EventLog", drivers.Rows.Keys);
        for (var i = 0; i < drivers.Table.Count; i++)
        {
            Assert.Equal(0u, drivers.Table.ServiceType[i] & Win32Types);
        }
    }

    [Fact]
    public void Scan_LeavesOutIgnoredServices()
    {
        var ignore = GlobalIgnoreList.Build(["SVC:EventLog", "SVC:Win*"]);

        var scan = ScanResult.Run(new ServicesScanOptions { Ignore = ignore.Matcher });

        Assert.DoesNotContain("EventLog", scan.Rows.Keys);
        Assert.DoesNotContain(scan.Rows.Keys, n => n.StartsWith("Win", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("RpcSs", scan.Rows.Keys);
    }

    [Theory]
    [InlineData("CDPUserSvc_1a2b3c", "Connected Devices Platform User Service_1a2b3c", 0xE0u, "CDPUserSvc_*", "Connected Devices Platform User Service")]
    [InlineData("CDPUserSvc_1a2b3c", "Other display", 0xE0u, "CDPUserSvc_*", "Other display")]
    [InlineData("Some_Service", "Some Service", 0x10u, "Some_Service", "Some Service")]
    public void NormalizeNames_ReplacesPerUserSuffix(string name, string display, uint type, string expectedName, string expectedDisplay)
    {
        var (normalized, normalizedDisplay) = ServicesScanner.NormalizeNames(name, display, type);

        Assert.Equal(expectedName, normalized);
        Assert.Equal(expectedDisplay, normalizedDisplay);
    }

    private sealed record ScanResult(ServiceTable Table, NameTable Names, Dictionary<string, int> Rows)
    {
        public static ScanResult Run(ServicesScanOptions options)
        {
            var names = new NameTable();
            var builder = new ServiceTableBuilder(names);
            var skipped = new SkippedCollector();
            new ServicesScanner(builder, new Lock(), skipped).Scan(options);
            var table = builder.Build();
            var rows = Enumerable.Range(0, table.Count)
                .ToDictionary(i => names.GetString(table.Name[i]), StringComparer.OrdinalIgnoreCase);
            return new ScanResult(table, names, rows);
        }
    }
}
