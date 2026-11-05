using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Tests.Model;

/// <summary>Permission changes: what is recorded, how it is compared, and that it survives a file round trip.</summary>
public class PermissionsTests
{
    private const uint Dir = FsTree.DirectoryAttribute;
    private const uint Archive = 0x20;

    // Owner Administrators; SYSTEM full control set here; Users read inherited from the parent.
    private const string WithInherited = "O:BAD:(A;OICI;FA;;;SY)(A;OICIID;0x1200a9;;;BU)";
    private const string Locked = "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;BU)";
    private const string InheritedOnly = "O:BAD:(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;BU)";
    private const string InheritedOnlyChanged = "O:BAD:(A;OICIID;FA;;;SY)(A;OICIID;0x1301bf;;;BU)";

    [Fact]
    public void Normalize_DropsInheritedEntries()
    {
        Assert.Equal("O:BAD:(A;OICI;FA;;;SY)", Permissions.ToSddl(Permissions.NormalizeSddl(WithInherited)));
        Assert.Equal("O:BAD:", Permissions.ToSddl(Permissions.NormalizeSddl(InheritedOnly)));
        Assert.Equal(Permissions.NormalizeSddl(InheritedOnly), Permissions.NormalizeSddl(InheritedOnlyChanged));
    }

    [Fact]
    public void Normalize_KeepsProtectedFlagAndOrder()
    {
        var normalized = Permissions.NormalizeSddl(Locked);
        var set = Permissions.Parse(normalized)!;

        Assert.True(set.InheritanceDisabled);
        Assert.Equal("S-1-5-18", set.OwnerSid);
        Assert.Equal(["S-1-5-18", "S-1-5-32-544", "S-1-5-32-545"], set.Entries.Select(e => e.Sid));
        Assert.Equal("Modify", Permissions.DescribeRights(set.Entries[2].Rights, PermissionTarget.FileSystem));
        Assert.Equal("this folder, subfolders and files", set.Entries[2].DescribeAppliesTo(PermissionTarget.FileSystem));
    }

    [Fact]
    public void Normalize_BinaryAndTextFormsAgree()
    {
        var raw = new System.Security.AccessControl.RawSecurityDescriptor(Locked);
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);

        Assert.Equal(Permissions.NormalizeSddl(Locked), Permissions.Normalize(bytes));
    }

    [Theory]
    [InlineData(0x1F01FF, "Full control")]
    [InlineData(0x1301BF, "Modify")]
    [InlineData(0x1200A9, "Read & execute")]
    [InlineData(0x120089, "Read")]
    [InlineData(0x10000000, "Full control")]
    [InlineData(0x1, "Special (0x1)")]
    public void DescribeRights_FileSystem(int rights, string expected) =>
        Assert.Equal(expected, Permissions.DescribeRights(rights, PermissionTarget.FileSystem));

    [Fact]
    public void Summarize_NamesWhatChanged()
    {
        var older = Permissions.NormalizeSddl("O:BAD:");
        var newer = Permissions.NormalizeSddl(Locked);

        var summary = Permissions.Summarize(older, newer, PermissionTarget.FileSystem);

        Assert.Equal("Owner changed, Inheritance disabled, +3 entries", summary);
        Assert.Equal("Inherited", Permissions.DescribeKind(older));
        Assert.Equal("Own", Permissions.DescribeKind(newer));
    }

    [Fact]
    public void Compare_ReportsOnlyItemsWhoseOwnPermissionsChanged()
    {
        var a = Build(folder: InheritedOnly, file: InheritedOnly, key: "O:BAD:");
        var b = Build(folder: Locked, file: InheritedOnlyChanged, key: "O:BAD:(A;CI;KA;;;BU)");

        var diff = SnapshotComparer.Compare(a, b);

        var fs = diff.FileSystem;
        var changed = Enumerable.Range(0, fs.Count).Where(n => tree(fs, n) == ChangeKind.Modified).ToList();
        var folder = Assert.Single(changed);
        Assert.Equal(@"C:\ProgramData\Contoso", diff.FullPath(fs, folder));
        Assert.Equal(ChangedFields.Permissions, fs.Fields(folder));

        var reg = diff.Registry;
        var key = Assert.Single(Enumerable.Range(0, reg.Count), n => tree(reg, n) == ChangeKind.Modified);
        Assert.Equal(@"HKLM\SOFTWARE\Contoso", diff.FullPath(reg, key));
        Assert.Equal(ChangedFields.Permissions, reg.Fields(key));

        static ChangeKind tree(DiffTree t, int n) => t.ChangeFromOlder(n);
    }

    [Fact]
    public void Export_LeavesOutFilesWhoseOnlyChangeIsPermissions()
    {
        var a = Build(folder: InheritedOnly, file: "O:BAD:", key: "O:BAD:");
        var b = Build(folder: InheritedOnly, file: "O:BAD:(A;;FA;;;BU)", key: "O:BAD:");
        var diff = SnapshotComparer.Compare(a, b);
        var fs = diff.FileSystem;
        var file = Assert.Single(Enumerable.Range(0, fs.Count), n => fs.ChangeFromOlder(n) == ChangeKind.Modified);
        Assert.Equal(ChangedFields.Permissions, fs.Fields(file));

        Assert.Empty(SysStateTracer.Core.Export.DiffExport.Files(diff, [file]));
    }

    [Fact]
    public void Normalize_StoresSidsNotAliases()
    {
        // SDDL writes a domain's Administrators as "DA", which means another SID on another computer.
        const string domainAdmins = "S-1-5-21-1004336348-1177238915-682003330-512";
        var recorded = Permissions.NormalizeSddl($"O:{domainAdmins}D:(A;;FA;;;{domainAdmins})");

        Assert.Equal(domainAdmins, Permissions.Parse(recorded)!.OwnerSid);
    }

    [Fact]
    public void Compare_IgnoresPermissionsWhenOneSnapshotHasNone()
    {
        var a = Build(folder: null, file: null, key: null);
        var b = Build(folder: Locked, file: Locked, key: "O:BAD:(A;CI;KA;;;BU)");

        var diff = SnapshotComparer.Compare(a, b);

        Assert.Equal(0, diff.FileSystemCounts.Total);
        Assert.Equal(0, diff.RegistryCounts.Total);
    }

    [Fact]
    public void Compare_SamePermissions_NoDifferences()
    {
        var diff = SnapshotComparer.Compare(Build(Locked, InheritedOnly, "O:BAD:"), Build(Locked, InheritedOnly, "O:BAD:"));

        Assert.Equal(0, diff.FileSystem.Count);
        Assert.Equal(0, diff.Registry.Count);
    }

    [Fact]
    public void WriteThenLoad_KeepsPermissions()
    {
        using var dir = new TempDirectory();
        var path = dir.File("permissions.stsnap");
        var original = Build(Locked, InheritedOnly, "O:BAD:(A;CI;KA;;;BU)");

        SnapshotWriter.Write(path, original);
        var loaded = SnapshotReader.Load(path);

        Assert.True(loaded.FileSystem.HasSecurity);
        Assert.True(loaded.Registry.HasSecurity);
        Assert.Equal(original.FileSystem.Digest.ToArray(), loaded.FileSystem.Digest.ToArray());
        Assert.Equal(Text(original.Names, original.FileSystem.Security), Text(loaded.Names, loaded.FileSystem.Security));
        Assert.Equal(Text(original.Names, original.Registry.Security), Text(loaded.Names, loaded.Registry.Security));
        Assert.Equal(0, SnapshotComparer.Compare(original, loaded).FileSystem.Count);
    }

    [Fact]
    public void WriteThenLoad_WithoutPermissions_HasNone()
    {
        using var dir = new TempDirectory();
        var path = dir.File("plain.stsnap");
        SnapshotWriter.Write(path, Build(null, null, null));

        var loaded = SnapshotReader.Load(path);

        Assert.False(loaded.FileSystem.HasSecurity);
        Assert.False(loaded.Registry.HasSecurity);
    }

    private static string?[] Text(NameTable names, ReadOnlySpan<int> ids)
    {
        var result = new string?[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            result[i] = ids[i] < 0 ? null : names.GetString(ids[i]);
        }

        return result;
    }

    private static long T(int day) => new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>C:\ProgramData\Contoso with one file, and HKLM\SOFTWARE\Contoso; null = permissions not recorded.</summary>
    private static Snapshot Build(string? folder, string? file, string? key)
    {
        var names = new NameTable();
        var fs = new FsTreeBuilder(names);
        var c = fs.AddRoot("C:", Dir, T(1));
        var data = fs.Add(c, "ProgramData", Dir, 0, T(1));
        var contoso = fs.Add(data, "Contoso", Dir, 0, T(1));
        var license = fs.Add(contoso, "license.dat", Archive, 1024, T(1));
        if (folder is not null)
        {
            fs.SetSecurity(c, Permissions.NormalizeSddl("O:SYD:P(A;OICI;FA;;;SY)"));
            fs.SetSecurity(data, Permissions.NormalizeSddl(InheritedOnly));
            fs.SetSecurity(contoso, Permissions.NormalizeSddl(folder));
            fs.SetSecurity(license, Permissions.NormalizeSddl(file!));
        }

        var reg = new RegTreeBuilder(names);
        var hklm = reg.AddRoot("HKLM", T(1));
        var software = reg.AddKey(hklm, "SOFTWARE", T(1));
        var vendor = reg.AddKey(software, "Contoso", T(1));
        reg.AddValue(vendor, "Version", 4, BitConverter.GetBytes(1));
        if (key is not null)
        {
            reg.SetSecurity(hklm, Permissions.NormalizeSddl("O:SYD:P(A;CI;KA;;;SY)"));
            reg.SetSecurity(software, Permissions.NormalizeSddl("O:SYD:"));
            reg.SetSecurity(vendor, Permissions.NormalizeSddl(key));
        }

        var snapshot = new Snapshot(new SnapshotMeta(), names, fs.Build(), reg.Build(), new ServiceTableBuilder(names).Build());
        names.Freeze();
        return snapshot;
    }
}
