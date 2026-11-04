using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Tests.Comparison;

/// <summary>System and User environment variables: compared by scope and name, stored, filtered and ignored (ENV:).</summary>
public class EnvironmentComparisonTests
{
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;

    [Fact]
    public void Variables_EveryChangeKind_ByScopeAndName()
    {
        var older = Build(
            (EnvironmentScope.System, "Path", @"C:\Windows", RegExpandSz),
            (EnvironmentScope.System, "OS", "Windows_NT", RegSz),
            (EnvironmentScope.User, "TEMP", @"C:\Temp", RegExpandSz),
            (EnvironmentScope.User, "OLD", "x", RegSz));
        var newer = Build(
            (EnvironmentScope.System, "PATH", @"C:\Windows;C:\Tools", RegExpandSz), // value changed (name case too)
            (EnvironmentScope.System, "OS", "Windows_NT", RegExpandSz),             // type changed
            (EnvironmentScope.User, "TEMP", @"C:\Temp", RegExpandSz),               // the same
            (EnvironmentScope.User, "Path", @"C:\Users\me\bin", RegSz));          // new: a User Path besides the System one

        var diff = SnapshotComparer.Compare(older, newer);
        var env = diff.Environment;
        var byKey = Enumerable.Range(0, env.Count).ToDictionary(n => diff.FullPath(env, n), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(4, env.Count);
        Assert.Equal(ChangeKind.Modified, env.Change(byKey[@"System\Path"]));
        Assert.Equal(ChangedFields.ValueData, env.Fields(byKey[@"System\Path"]));
        Assert.Equal(ChangedFields.ValueType, env.Fields(byKey[@"System\OS"]));
        Assert.Equal(ChangeKind.New, env.Change(byKey[@"User\Path"]));
        Assert.Equal(ChangeKind.Deleted, env.Change(byKey[@"User\OLD"]));
        Assert.Equal(new DiffCounts(1, 1, 2), diff.EnvironmentCounts);
        Assert.Equal(DiffItemKind.Variable, env.Kind(byKey[@"User\Path"]));
        Assert.Equal("Path", diff.Name(env, byKey[@"User\Path"]));
        Assert.Equal(@"ENV:User\Path", DiffItems.IgnoreRule(diff, new DiffItem(DiffArea.Environment, byKey[@"User\Path"])));
        Assert.Contains(@"C:\Windows;C:\Tools", DiffItems.Column(diff, new DiffItem(DiffArea.Environment, byKey[@"System\Path"]), right: true), StringComparison.Ordinal);

        var swapped = diff.Swap();
        Assert.Equal(diff.EnvironmentCounts.New, swapped.EnvironmentCounts.Deleted);
    }

    [Fact]
    public void Variables_OnlyOneSnapshotHasThem_AreNotCompared()
    {
        // A snapshot from before 1.3 has no environment variables: they must not all look new.
        var older = Build();
        var newer = Build((EnvironmentScope.System, "Path", @"C:\Windows", RegExpandSz));

        var diff = SnapshotComparer.Compare(older, newer);

        Assert.Equal(0, diff.Environment.Count);
    }

    [Fact]
    public void IgnoreRules_LeaveVariablesOut()
    {
        var older = Build((EnvironmentScope.User, "OneDrive", "a", RegSz), (EnvironmentScope.System, "Path", "a", RegSz), (EnvironmentScope.User, "Keep", "a", RegSz));
        var newer = Build((EnvironmentScope.User, "OneDrive", "b", RegSz), (EnvironmentScope.System, "Path", "b", RegSz), (EnvironmentScope.User, "Keep", "b", RegSz));
        var ignore = GlobalIgnoreList.Build([@"ENV:User\OneDrive", "ENV:System"]).Matcher;

        var diff = SnapshotComparer.Compare(older, newer, ignore);

        var node = Assert.Single(Enumerable.Range(0, diff.Environment.Count));
        Assert.Equal(@"User\Keep", diff.FullPath(diff.Environment, node));
    }

    [Fact]
    public void Filter_KeepsOnlyTheNamedVariables()
    {
        var older = Build((EnvironmentScope.System, "Path", "a", RegSz), (EnvironmentScope.User, "TEMP", "a", RegSz));
        var newer = Build((EnvironmentScope.System, "Path", "b", RegSz), (EnvironmentScope.User, "TEMP", "b", RegSz));
        var diff = SnapshotComparer.Compare(older, newer);

        Assert.True(DiffFilter.TryCreate([], [], [], [@"System\Path"], out var filter, out var error), error);
        var filtered = filter.Apply(diff);

        var node = Assert.Single(Enumerable.Range(0, filtered.Environment.Count));
        Assert.Equal(@"System\Path", filtered.FullPath(filtered.Environment, node));
        Assert.Equal(1, filter.Entries[0].Matched);
        Assert.Equal(0, filtered.Registry.Count);
    }

    [Fact]
    public void Storage_RoundTripsVariables_AndOldFilesHaveNone()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "env.stsnap");
        var snapshot = Build((EnvironmentScope.User, "TEMP", @"%USERPROFILE%\Temp", RegExpandSz), (EnvironmentScope.System, "Path", @"C:\Windows", RegExpandSz));
        SnapshotWriter.Write(path, snapshot);

        var loaded = SnapshotReader.Load(path);
        Assert.Equal(2, loaded.Environment.Count);
        Assert.Equal(EnvironmentScope.System, loaded.Environment.Scope[0]); // System first
        Assert.Equal(@"User\TEMP", loaded.Environment.Key(1, loaded.Names));
        Assert.Equal(@"%USERPROFILE%\Temp", loaded.Names.GetString(loaded.Environment.Value[1]));
        Assert.Equal(RegExpandSz, loaded.Environment.ValueType[1]);
        Assert.Equal(2, loaded.Meta.Counts.EnvironmentVariables);

        // Without the part (or in a file written before 1.3, which has no such sections): an empty table.
        Assert.Equal(0, SnapshotReader.Load(path, SnapshotParts.FileSystem).Environment.Count);
        var none = Path.Combine(temp.Path, "none.stsnap");
        SnapshotWriter.Write(none, Build());
        Assert.Equal(0, SnapshotReader.Load(none).Environment.Count);
    }

    [Fact]
    public void HtmlReport_ListsVariables()
    {
        var diff = SnapshotComparer.Compare(
            Build((EnvironmentScope.System, "Path", "a", RegSz)),
            Build((EnvironmentScope.System, "Path", "b", RegSz)));
        using var writer = new StringWriter();

        HtmlReport.Write(diff, writer);

        var html = writer.ToString();
        Assert.Contains("Environment variables", html, StringComparison.Ordinal);
        Assert.Contains(@"System\\Path", html, StringComparison.Ordinal); // JSON-escaped
    }

    private static Snapshot Build(params (EnvironmentScope Scope, string Name, string Value, uint Type)[] variables)
    {
        var names = new NameTable();
        var env = new EnvironmentTableBuilder(names);
        foreach (var (scope, name, value, type) in variables)
        {
            env.Add(scope, name, value, type);
        }

        var snapshot = new Snapshot(new SnapshotMeta { Name = "env" }, names, environment: env.Build());
        names.Freeze();
        return snapshot;
    }
}
