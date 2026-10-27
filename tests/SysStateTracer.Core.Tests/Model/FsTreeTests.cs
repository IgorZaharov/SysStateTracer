using SysStateTracer.Core.Model;
using static SysStateTracer.Core.Tests.SampleSnapshots;

namespace SysStateTracer.Core.Tests.Model;

public class FsTreeTests
{
    [Fact]
    public void Build_StoresPreOrderWithSortedSiblings()
    {
        var snapshot = SampleSnapshots.Create();
        var fs = snapshot.FileSystem;

        var paths = Enumerable.Range(0, fs.Count).Select(i => fs.GetFullPath(i, snapshot.Names)).ToArray();

        Assert.Equal(
            new[]
            {
                @"C:\",
                @"C:\apps",
                @"C:\boot",
                @"C:\pagefile.sys",
                @"C:\ProgramData",
                @"C:\Windows",
                @"C:\Windows\notepad.exe",
                @"C:\Windows\System32",
                @"C:\Windows\System32\kernel32.dll",
                @"D:\",
                @"D:\Data",
            },
            paths);
    }

    [Fact]
    public void Build_ComputesDescendantsAndChildCounts()
    {
        var snapshot = SampleSnapshots.Create();
        var fs = snapshot.FileSystem;

        Assert.Equal(8, fs.Descendants[0]);   // C:\ has 8 entries below it
        Assert.Equal(4, fs.ChildFolders[0]);  // apps, boot, ProgramData, Windows
        Assert.Equal(1, fs.ChildFiles[0]);    // pagefile.sys
        Assert.Equal(3, fs.Descendants[5]);   // Windows
        Assert.Equal(1, fs.ChildFolders[5]);
        Assert.Equal(1, fs.ChildFiles[5]);
        Assert.Equal(-1, fs.Parent[0]);
        Assert.Equal(-1, fs.Parent[9]);
        Assert.Equal(5, fs.Parent[7]);
    }

    [Fact]
    public void Build_FoldersHaveNoSize()
    {
        var names = new NameTable();
        var builder = new FsTreeBuilder(names);
        var root = builder.AddRoot("C:", Dir, Time(1));
        builder.Add(root, "folder", Dir, 12345, Time(1));

        var fs = builder.Build();

        Assert.Equal(0, fs.Size[1]);
        Assert.True(fs.IsDirectory(1));
    }

    [Fact]
    public void Digest_DoesNotDependOnInsertionOrder()
    {
        static FsTree Build(bool reversed)
        {
            var names = new NameTable();
            var b = new FsTreeBuilder(names);
            var root = b.AddRoot("C:", Dir, Time(1));
            var files = new[] { ("a.txt", 1L), ("b.txt", 2L), ("c.txt", 3L) };
            foreach (var (name, size) in reversed ? Enumerable.Reverse(files) : files)
            {
                b.Add(root, name, Archive, size, Time(2));
            }

            return b.Build();
        }

        Assert.Equal(Build(false).Digest.ToArray(), Build(true).Digest.ToArray());
    }

    [Fact]
    public void Digest_ChangeReachesAncestorsOnly()
    {
        static FsTree Build(long kernelSize)
        {
            var names = new NameTable();
            var b = new FsTreeBuilder(names);
            var c = b.AddRoot("C:", Dir, Time(1));
            var windows = b.Add(c, "Windows", Dir, 0, Time(5));
            b.Add(windows, "kernel32.dll", Archive, kernelSize, Time(3));
            b.Add(c, "Users", Dir, 0, Time(5));
            return b.Build();
        }

        var before = Build(100);
        var after = Build(200);

        // Sorted layout: 0 C:\, 1 Users, 2 Windows, 3 Windows\kernel32.dll
        Assert.NotEqual(before.Digest[0], after.Digest[0]);
        Assert.Equal(before.Digest[1], after.Digest[1]);
        Assert.NotEqual(before.Digest[2], after.Digest[2]);
        Assert.NotEqual(before.Digest[3], after.Digest[3]);
    }

    [Fact]
    public void Add_RejectsParentThatDoesNotExistYet()
    {
        var builder = new FsTreeBuilder(new NameTable());

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(0, "x", 0, 0, 0));
    }
}
