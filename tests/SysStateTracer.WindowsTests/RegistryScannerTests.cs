using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Reg;

namespace SysStateTracer.WindowsTests;

public class RegistryScannerTests
{
    private const uint RegNone = 0;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegBinary = 3;
    private const uint RegDword = 4;
    private const uint RegMultiSz = 7;
    private const uint RegQword = 11;

    [Fact]
    public void Scan_ReadsAllValueTypes()
    {
        using var test = new TestRegistryKey();
        var binary = new byte[] { 1, 2, 3, 0, 255 };
        var medium = new byte[100_000];
        Random.Shared.NextBytes(medium);

        test.Key.SetValue(string.Empty, "default value");
        test.Key.SetValue("sz", "hello", RegistryValueKind.String);
        test.Key.SetValue("expand", "%windir%\\x", RegistryValueKind.ExpandString);
        test.Key.SetValue("binary", binary, RegistryValueKind.Binary);
        test.Key.SetValue("dword", unchecked((int)0xDEADBEEF), RegistryValueKind.DWord);
        test.Key.SetValue("qword", 0x0102030405060708L, RegistryValueKind.QWord);
        test.Key.SetValue("multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
        test.Key.SetValue("none", Array.Empty<byte>(), RegistryValueKind.None);
        test.Key.SetValue("medium", medium, RegistryValueKind.Binary);
        test.Key.SetValue("Ünïcødé ✓", "x");

        var scan = ScanResult.Run([test.HkcuPath]);

        var values = scan.Values(test.StoredPath);
        Assert.Equal(10, values.Count);
        AssertValue(scan, values[string.Empty], RegSz, Utf16Z("default value"));
        AssertValue(scan, values["sz"], RegSz, Utf16Z("hello"));
        AssertValue(scan, values["expand"], RegExpandSz, Utf16Z("%windir%\\x"));
        AssertValue(scan, values["binary"], RegBinary, binary);
        AssertValue(scan, values["dword"], RegDword, BitConverter.GetBytes(0xDEADBEEF));
        AssertValue(scan, values["qword"], RegQword, BitConverter.GetBytes(0x0102030405060708L));
        AssertValue(scan, values["multi"], RegMultiSz, Encoding.Unicode.GetBytes("one\0two\0\0"));
        AssertValue(scan, values["none"], RegNone, []);
        AssertValue(scan, values["medium"], RegBinary, medium);
        AssertValue(scan, values["Ünïcødé ✓"], RegSz, Utf16Z("x"));
        Assert.Equal(0, scan.Skipped.Count);
    }

    [Fact]
    public void Scan_StoresOnlyLengthOfValuesOverOneMegabyte()
    {
        using var test = new TestRegistryKey();
        var big = new byte[RegTreeBuilder.MaxStoredValueSize + 1];
        Random.Shared.NextBytes(big);
        test.Key.SetValue("big", big, RegistryValueKind.Binary);

        var scan = ScanResult.Run([test.HkcuPath]);

        var value = scan.Values(test.StoredPath)["big"];
        Assert.False(scan.Tree.IsValueDataStored(value));
        Assert.Equal(big.Length, scan.Tree.ValueDataLength[value]);
        Assert.NotEqual(0UL, scan.Tree.ValueDataHash[value]);
    }

    [Fact]
    public void Scan_ReadsWideAndDeepTrees()
    {
        using var test = new TestRegistryKey();
        using (var wide = test.Key.CreateSubKey("wide"))
        {
            for (var i = 0; i < 300; i++)
            {
                using var child = wide.CreateSubKey($"k{i:D3}");
                child.SetValue("i", i);
                using var grandchild = child.CreateSubKey("g");
            }
        }

        var deepPath = string.Join('\\', Enumerable.Range(0, 20).Select(i => $"d{i}"));
        using (var deep = test.Key.CreateSubKey(deepPath))
        {
            deep.SetValue("bottom", "yes");
        }

        var scan = ScanResult.Run([test.HkcuPath]);

        var below = scan.KeysBelow(test.StoredPath);
        Assert.Equal(1 + 300 + 300 + 20, below.Count);
        Assert.Equal(300 + 300, scan.Tree.Descendants[scan.Keys[test.StoredPath + @"\wide"]]);
        Assert.Single(scan.Values(test.StoredPath + @"\wide\k123"));
        Assert.Contains("bottom", scan.Values(test.StoredPath + "\\" + deepPath).Keys);

        // Pre-order with sorted siblings.
        var wideKey = scan.Keys[test.StoredPath + @"\wide"];
        Assert.Equal(test.StoredPath + @"\wide\k000", scan.Tree.GetFullPath(wideKey + 1, scan.Names));
    }

    [Fact]
    public void Scan_LeavesOutIgnoredKeysAndValues()
    {
        using var test = new TestRegistryKey();
        using (var keep = test.Key.CreateSubKey("keep"))
        {
            keep.SetValue("a", 1);
            keep.SetValue("Volatile", 2);
        }

        using (test.Key.CreateSubKey(@"skip\inner"))
        {
        }

        var ignore = GlobalIgnoreList.Build([$@"REG:{test.HkcuPath}\skip", $@"REG:{test.HkcuPath}\keep\Volatile"]);
        Assert.Empty(ignore.Errors);

        var scan = ScanResult.Run([test.HkcuPath], ignore.Matcher);

        Assert.Equal(new[] { "keep" }, scan.KeysBelow(test.StoredPath).Keys);
        Assert.Equal(new[] { "a" }, scan.Values(test.StoredPath + @"\keep").Keys);
    }

    [Fact]
    public void Scan_ReadsKeysThatShutOutEveryone()
    {
        using var test = new TestRegistryKey();
        using (var locked = test.Key.CreateSubKey("locked"))
        {
            locked.SetValue("secret", "x");
            using (locked.CreateSubKey("inner"))
            {
            }

            // Protected, empty permission list: nobody may read it (the owner can still change permissions).
            var closed = new RegistrySecurity();
            closed.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            locked.SetAccessControl(closed);
        }

        try
        {
            var scan = ScanResult.Run([test.HkcuPath]);

            if (SysStateTracer.Core.Interop.ProcessPrivileges.IsElevated)
            {
                Assert.Contains("secret", scan.Values(test.StoredPath + @"\locked").Keys);
                Assert.Contains(test.StoredPath + @"\locked\inner", scan.Keys.Keys);
                Assert.Equal(0, scan.Skipped.Count);
            }
            else
            {
                Assert.Equal(1, scan.Skipped.Count);
            }
        }
        finally
        {
            using var reopen = test.Key.OpenSubKey("locked", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions)!;
            var open = new RegistrySecurity();
            open.AddAccessRule(new RegistryAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            reopen.SetAccessControl(open);
        }
    }

    [Fact]
    public void Scan_MissingRootIsSkipped()
    {
        var scan = ScanResult.Run([@"HKCU\Software\SysStateTracerTests\does-not-exist-" + Guid.NewGuid().ToString("N")]);

        Assert.Equal(1, scan.Skipped.Count);
        Assert.Equal(0, scan.Tree.ValueCount);
    }

    [Fact]
    public void Scan_StopsWhenCancelled()
    {
        using var test = new TestRegistryKey();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ScanResult.Run([test.HkcuPath], cancellationToken: cts.Token));
    }

    [Fact]
    public void Scan_ReadsMachineKeys()
    {
        var scan = ScanResult.Run([@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"]);

        var values = scan.Values(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        Assert.Contains("ProductName", values.Keys);
        Assert.Contains("CurrentBuild", values.Keys);
    }

    [Fact]
    public void NormalizeRoots_MapsHivesAndDropsNestedRoots()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

        var roots = RegistryScanner.NormalizeRoots(["HKEY_LOCAL_MACHINE\\SOFTWARE\\", "hklm\\SOFTWARE\\Classes", "HKCU\\Software", "HKU"]);

        Assert.Equal(new[] { "HKU", @"HKLM\SOFTWARE" }, roots);
        Assert.Equal(new[] { $@"HKU\{sid}\Software" }, RegistryScanner.NormalizeRoots(["HKEY_CURRENT_USER\\Software"]));
    }

    private static byte[] Utf16Z(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static void AssertValue(ScanResult scan, int value, uint type, byte[] data)
    {
        Assert.Equal(type, scan.Tree.ValueDataType[value]);
        Assert.Equal(data.Length, scan.Tree.ValueDataLength[value]);
        Assert.True(scan.Tree.IsValueDataStored(value));
        Assert.Equal(data, scan.Tree.GetValueData(value).ToArray());
    }

    private sealed record ScanResult(RegTree Tree, NameTable Names, SkippedCollector Skipped, Dictionary<string, int> Keys)
    {
        public static ScanResult Run(string[] roots, IgnoreMatcher? ignore = null, CancellationToken cancellationToken = default)
        {
            var names = new NameTable();
            var builder = new RegTreeBuilder(names);
            var skipped = new SkippedCollector();
            var scanner = new RegistryScanner(builder, new Lock(), skipped);
            scanner.Scan(
                new RegistryScanOptions { Roots = roots, Ignore = ignore ?? IgnoreMatcher.Empty, MaxDegreeOfParallelism = 4 },
                cancellationToken);

            var tree = builder.Build();
            var keys = Enumerable.Range(0, tree.KeyCount)
                .ToDictionary(i => tree.GetFullPath(i, names), StringComparer.OrdinalIgnoreCase);
            return new ScanResult(tree, names, skipped, keys);
        }

        public Dictionary<string, int> Values(string keyPath)
        {
            var key = Keys[keyPath];
            var first = Tree.FirstValue[key];
            return Enumerable.Range(first, Tree.KeyValueCount[key])
                .ToDictionary(v => Names.GetString(Tree.ValueName[v]), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Keys inside <paramref name="root"/>, keyed by path relative to it.</summary>
        public Dictionary<string, int> KeysBelow(string root)
        {
            var prefix = root + "\\";
            return Keys
                .Where(k => k.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(k => k.Key[prefix.Length..], k => k.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
