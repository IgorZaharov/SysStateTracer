using Microsoft.Win32;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Env;

namespace SysStateTracer.WindowsTests;

/// <summary>Reading the System and User environment variables from the registry (read only).</summary>
public class EnvironmentScannerTests
{
    [Fact]
    public void Scan_ReadsSystemAndUserVariables_AsWritten()
    {
        var (table, names) = Scan(IgnoreMatcher.Empty);

        var keys = Enumerable.Range(0, table.Count).ToDictionary(i => table.Key(i, names), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"System\Path", keys.Keys);
        Assert.Contains(@"System\windir", keys.Keys);

        // Not expanded: the value as the Environment Variables window shows it.
        using var system = Registry.LocalMachine.OpenSubKey(EnvironmentScanner.SystemKey)!;
        var raw = (string)system.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
        var path = keys[@"System\Path"];
        Assert.Equal(raw, names.GetString(table.Value[path]));
        Assert.Equal((uint)system.GetValueKind("Path"), table.ValueType[path]);

        // System first, then the user's own, each sorted by name.
        Assert.Equal(EnvironmentScope.System, table.Scope[0]);
        using var user = Registry.CurrentUser.OpenSubKey(EnvironmentScanner.UserKey);
        var userCount = user?.GetValueNames().Count(n => n.Length > 0) ?? 0;
        Assert.Equal(userCount, Enumerable.Range(0, table.Count).Count(i => table.Scope[i] == EnvironmentScope.User));
    }

    [Fact]
    public void Scan_LeavesOutVariablesMatchingEnvRules()
    {
        var ignore = GlobalIgnoreList.Build([@"ENV:System\Path", "ENV:User"]).Matcher;

        var (table, names) = Scan(ignore);

        Assert.DoesNotContain(Enumerable.Range(0, table.Count), i => table.Key(i, names).Equals(@"System\Path", StringComparison.OrdinalIgnoreCase));
        Assert.All(Enumerable.Range(0, table.Count), i => Assert.Equal(EnvironmentScope.System, table.Scope[i]));
        Assert.True(table.Count > 0);
    }

    private static (EnvironmentTable Table, NameTable Names) Scan(IgnoreMatcher ignore)
    {
        var names = new NameTable();
        var builder = new EnvironmentTableBuilder(names);
        var skipped = new SkippedCollector();
        var count = EnvironmentScanner.Scan(builder, ignore, skipped);
        var table = builder.Build();
        Assert.Equal(count, table.Count);
        Assert.Equal(0, skipped.Count);
        return (table, names);
    }
}
