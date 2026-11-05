using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Core.Scanning.Reg;

namespace SysStateTracer.WindowsTests;

/// <summary>Permissions read from real folders and keys, and a permission change found by the comparison.</summary>
public class PermissionsScanTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void FolderListing_RecordsOwnEntriesOnly()
    {
        using var folder = new TestFolder();
        folder.CreateFile(@"locked\inside.txt", 10);

        // %TEMP% does not use automatic inheritance, so new files there hold copies of the folder's entries that are
        // not marked inherited. Writing the folder's permissions once turns them into inherited ones, as Windows does
        // for folders made in Explorer, so the test starts from a normal folder.
        var locked = new DirectoryInfo(folder.Combine("locked"));
        var probe = new FileSystemAccessRule(Users, FileSystemRights.ReadData, AccessControlType.Allow);
        var security = locked.GetAccessControl();
        security.AddAccessRule(probe);
        locked.SetAccessControl(security);
        security.RemoveAccessRule(probe);
        locked.SetAccessControl(security);
        var before = Scan(folder.Path, permissions: true);

        GrantUsersModify(folder.Combine("locked"));
        var after = Scan(folder.Path, permissions: true);

        Assert.True(after.FileSystem.HasSecurity);
        var diff = SnapshotComparer.Compare(before, after);
        var fs = diff.FileSystem;
        var modified = Enumerable.Range(0, fs.Count).Where(n => fs.ChangeFromOlder(n) == ChangeKind.Modified).ToList();
        Assert.True(modified.Count == 1, string.Join(" | ", modified.Select(n =>
            $"{diff.FullPath(fs, n)} {fs.Fields(n)} {Permissions.ToSddl(Recorded(before, diff.FullPath(fs, n)))} -> {Permissions.ToSddl(Recorded(after, diff.FullPath(fs, n)))}")));
        var node = modified[0];
        Assert.EndsWith(@"\locked", diff.FullPath(fs, node), StringComparison.OrdinalIgnoreCase);
        Assert.True(fs.Fields(node).HasFlag(ChangedFields.Permissions));

        // The file inside only inherited the new entry: not reported, and it has no own entries.
        var file = Permissions.Parse(Recorded(after, folder.Combine(@"locked\inside.txt")));
        Assert.NotNull(file);
        Assert.DoesNotContain(file.Entries, e => e.Sid == Users.Value);
    }

    [Fact]
    public void WithoutPermissions_NothingIsRecorded()
    {
        using var folder = new TestFolder();
        folder.CreateFile("a.txt", 1);

        Assert.False(Scan(folder.Path, permissions: false).FileSystem.HasSecurity);
    }

    [Fact]
    public void Registry_KeyPermissionChange_IsFound()
    {
        using var test = new TestRegistryKey();
        using (test.Key.CreateSubKey("child"))
        {
        }

        var before = ScanRegistry(test.HkcuPath);
        using (var child = test.Key.OpenSubKey("child", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey)!)
        {
            var security = child.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(Users, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            child.SetAccessControl(security);
        }

        var after = ScanRegistry(test.HkcuPath);

        Assert.True(after.Registry.HasSecurity);
        var diff = SnapshotComparer.Compare(before, after);
        var reg = diff.Registry;
        var node = Assert.Single(Enumerable.Range(0, reg.Count), n => reg.ChangeFromOlder(n) == ChangeKind.Modified);
        Assert.EndsWith(@"\child", diff.FullPath(reg, node), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ChangedFields.Permissions, reg.Fields(node));
    }

    private static void GrantUsersModify(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string? Recorded(Snapshot snapshot, string path)
    {
        var fs = snapshot.FileSystem;
        for (var i = 0; i < fs.Count; i++)
        {
            if (string.Equals(fs.GetFullPath(i, snapshot.Names), path, StringComparison.OrdinalIgnoreCase))
            {
                return fs.Security[i] < 0 ? null : snapshot.Names.GetString(fs.Security[i]);
            }
        }

        return null;
    }

    private static Snapshot Scan(string root, bool permissions)
    {
        var names = new NameTable();
        var builder = new FsTreeBuilder(names);
        var scanner = new FileSystemScanner(builder, new Lock(), new SkippedCollector());
        scanner.Scan(new FileSystemScanOptions { Roots = [root], Method = FileSystemScanMethod.ListFolders, Permissions = permissions });
        var snapshot = new Snapshot(new SnapshotMeta(), names, builder.Build(), new RegTreeBuilder(names).Build(), new ServiceTableBuilder(names).Build());
        names.Freeze();
        return snapshot;
    }

    private static Snapshot ScanRegistry(string root)
    {
        var names = new NameTable();
        var builder = new RegTreeBuilder(names);
        var scanner = new RegistryScanner(builder, new Lock(), new SkippedCollector());
        scanner.Scan(new RegistryScanOptions { Roots = [root], Permissions = true });
        var snapshot = new Snapshot(new SnapshotMeta(), names, new FsTreeBuilder(names).Build(), builder.Build(), new ServiceTableBuilder(names).Build());
        names.Freeze();
        return snapshot;
    }
}
