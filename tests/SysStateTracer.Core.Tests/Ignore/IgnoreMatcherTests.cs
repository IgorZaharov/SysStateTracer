using System.Diagnostics;
using SysStateTracer.Core.Ignore;
using Xunit.Abstractions;

namespace SysStateTracer.Core.Tests.Ignore;

public class IgnoreMatcherTests(ITestOutputHelper output)
{
    private static IgnoreMatcher Matcher(params string[] lines)
    {
        var list = GlobalIgnoreList.Build(lines);
        Assert.Empty(list.Errors);
        return list.Matcher;
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"C:\Windows\Temp\", true)]
    [InlineData(@"c:\windows\TEMP\setup.log", true)]
    [InlineData(@"C:\Windows\Temp\a\b\c.txt", true)]
    [InlineData(@"\\?\C:\Windows\Temp\x", true)]
    [InlineData(@"C:\Windows\TempFiles", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"D:\Windows\Temp", false)]
    public void ExactRule_CoversFolderAndEverythingBelow(string path, bool ignored)
    {
        var matcher = Matcher(@"FS:C:\Windows\Temp");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.FileSystem, path));
    }

    [Fact]
    public void EntryCheck_LooksAtTheEntryOnly()
    {
        var matcher = Matcher(@"FS:C:\Windows\Temp", @"FS:*\AppData\Local\Temp");

        Assert.True(matcher.IsIgnoredEntry(IgnoreTarget.FileSystem, @"C:\Windows\Temp"));
        Assert.False(matcher.IsIgnoredEntry(IgnoreTarget.FileSystem, @"C:\Windows\Temp\x.log"));
        Assert.True(matcher.IsIgnoredEntry(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Temp"));
        Assert.False(matcher.IsIgnoredEntry(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Temp\x.tmp"));
    }

    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp\x\y.tmp", true)]
    [InlineData(@"C:\Windows\ServiceProfiles\LocalService\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\bob\AppData\Local\TempFiles", false)]
    [InlineData(@"C:\Users\bob\AppData\Local", false)]
    public void LeadingStar_SpansFolders(string path, bool ignored)
    {
        var matcher = Matcher(@"FS:*\AppData\Local\Temp");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.FileSystem, path));
    }

    [Theory]
    [InlineData(@"C:\Users\bob\x", true)]
    [InlineData(@"C:\Users\amy\x", true)]
    [InlineData(@"C:\Users\b\o\x", false)]
    [InlineData(@"C:\Users\bobby\x", false)]
    public void QuestionMark_MatchesOneCharacterButNotASeparator(string path, bool ignored)
    {
        var matcher = Matcher(@"FS:C:\Users\???\x");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.FileSystem, path));
    }

    [Theory]
    [InlineData(@"HKU\S-1-5-21-1000\Software\Test", true)]
    [InlineData(@"HKU\S-1-5-21-1000\Software\Test\Sub\Deeper", true)]
    [InlineData(@"HKU\.DEFAULT\Software\Test", true)]
    [InlineData(@"HKLM\Software\Test", false)]
    public void Hkcu_MeansEveryUser(string path, bool ignored)
    {
        var matcher = Matcher(@"REG:HKCU\Software\Test");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.Registry, path));
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\bam\State", true)]
    [InlineData(@"HKLM\SYSTEM\ControlSet002\Services\bam", true)]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\bamx", false)]
    [InlineData(@"HKLM\SYSTEM\Select", false)]
    public void CurrentControlSet_MatchesRealControlSets(string path, bool ignored)
    {
        var matcher = Matcher(@"REG:HKLM\SYSTEM\CurrentControlSet\Services\bam");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.Registry, path));
    }

    [Fact]
    public void Hkcr_MapsToMachineClasses()
    {
        var matcher = Matcher(@"REG:HKEY_CLASSES_ROOT\.txt");

        Assert.True(matcher.IsIgnored(IgnoreTarget.Registry, @"HKLM\SOFTWARE\Classes\.txt\ShellNew"));
    }

    [Theory]
    [InlineData("edgeupdate", true)]
    [InlineData("EdgeUpdateM", true)]
    [InlineData("MicrosoftEdgeElevationService", false)]
    [InlineData("spooler", true)]
    [InlineData("Spooler2", false)]
    public void ServiceRules_MatchWholeNames(string name, bool ignored)
    {
        var matcher = Matcher("SVC:edge*", "SVC:Spooler");

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.Service, name));
        Assert.Equal(ignored, matcher.IsIgnoredEntry(IgnoreTarget.Service, name));
    }

    [Fact]
    public void Targets_DoNotMix()
    {
        var matcher = Matcher(@"FS:C:\Data", "SVC:Data");

        Assert.False(matcher.IsIgnored(IgnoreTarget.Registry, @"C:\Data"));
        Assert.True(matcher.IsIgnored(IgnoreTarget.FileSystem, @"C:\Data"));
        Assert.False(matcher.IsIgnored(IgnoreTarget.FileSystem, "Data"));
    }

    [Fact]
    public void DuplicateRules_CountOnce()
    {
        var matcher = Matcher(@"FS:C:\Temp", @"fs:c:\temp\", "SVC:a*", "SVC:A*");

        Assert.Equal(2, matcher.RuleCount);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("a*c", "abbbc", true)]
    [InlineData("a*c", @"a\b\c", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", @"a\c", false)]
    [InlineData("*", "", true)]
    [InlineData("a*", "a", true)]
    [InlineData("*b*", "aaa", false)]
    [InlineData("ABC", "abc", true)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "axxbyy", false)]
    public void Glob_Cases(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, IgnoreMatcher.Glob(pattern, text));
    }

    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp\x.tmp", true)]
    [InlineData(@"C:\Users\bob\NTUSER.DAT", true)]
    [InlineData(@"C:\Users\bob\ntuser.dat.LOG1", true)]
    [InlineData(@"C:\Windows\Prefetch\CMD.EXE-12345678.pf", true)]
    [InlineData(@"C:\pagefile.sys", true)]
    [InlineData(@"C:\Program Files\7-Zip\7z.exe", false)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", false)]
    [InlineData(@"C:\Users\bob\Documents\report.docx", false)]
    public void DefaultRules_FileSystem(string path, bool ignored)
    {
        var matcher = GlobalIgnoreList.Build(DefaultIgnoreRules.RuleLines).Matcher;

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.FileSystem, path));
    }

    [Theory]
    [InlineData(@"HKLM\SAM\SAM\Domains", true)]
    [InlineData(@"HKLM\SECURITY\Policy", true)]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\bam\State\UserSettings", true)]
    [InlineData(@"HKU\S-1-5-21-1000\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count", true)]
    [InlineData(@"HKU\S-1-5-21-1000_Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", true)]
    [InlineData(@"HKLM\SOFTWARE\7-Zip", false)]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\Spooler", false)]
    public void DefaultRules_Registry(string path, bool ignored)
    {
        var matcher = GlobalIgnoreList.Build(DefaultIgnoreRules.RuleLines).Matcher;

        Assert.Equal(ignored, matcher.IsIgnored(IgnoreTarget.Registry, path));
    }

    [Theory]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Windows\Temp")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Windows\TEMP")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Temp")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Microsoft\Windows\Explorer\thumbcache_32.db")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Microsoft\Edge\User Data\Default\Code Cache")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\AppData\Local\Microsoft\Edge\User Data\Default\Preferences")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\NTUSER.DAT")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Users\bob\ntuser.ini")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Windows\System32\config\SYSTEM.LOG1")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Windows\System32\config\SYSTEM")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Windows\System32\config\TxR")]
    [InlineData(IgnoreTarget.FileSystem, "C:")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\pagefile.sys")]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Temp")]
    [InlineData(IgnoreTarget.Registry, @"HKLM\SAM")]
    [InlineData(IgnoreTarget.Registry, @"HKLM\SYSTEM\ControlSet001\Services\bam\State")]
    [InlineData(IgnoreTarget.Registry, @"HKLM\SYSTEM\ControlSet001\Services\bam")]
    [InlineData(IgnoreTarget.Registry, @"HKU\S-1-5-21-1000\Software\Microsoft\Windows\Shell\Bags")]
    [InlineData(IgnoreTarget.Registry, @"HKU\S-1-5-21-1000_Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache")]
    [InlineData(IgnoreTarget.Registry, @"HKLM\SOFTWARE\Classes\.txt")]
    [InlineData(IgnoreTarget.Service, "edgeupdate")]
    [InlineData(IgnoreTarget.Service, "Spooler")]
    public void EntryCheck_AgreesWithFullCheck_WhenParentIsNotIgnored(IgnoreTarget target, string path)
    {
        var lines = DefaultIgnoreRules.RuleLines.Concat(["SVC:edge*", @"FS:C:\Temp", @"REG:HKCR\.txt"]);
        var matcher = GlobalIgnoreList.Build(lines).Matcher;
        var slash = path.LastIndexOf('\\');
        var parentIgnored = target != IgnoreTarget.Service && slash > 0 && matcher.IsIgnored(target, path.AsSpan(0, slash));
        Assert.False(parentIgnored, "Test data must use entries whose parent is not ignored.");

        Assert.Equal(matcher.IsIgnored(target, path), matcher.IsIgnoredEntry(target, path));
    }

    [Fact]
    public void EntryCheck_HundredThousandPaths_IsFast()
    {
        var matcher = GlobalIgnoreList.Build(DefaultIgnoreRules.RuleLines.Concat(Enumerable.Range(0, 50).Select(i => $@"FS:C:\Extra\Rule{i}"))).Matcher;
        var paths = new string[100_000];
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i] = $@"C:\Users\user{i % 20}\Documents\Project{i % 500}\Sub{i % 7}\file{i}.txt";
        }

        // A real scan makes millions of checks, so what matters is the speed once the JIT has
        // fully optimized the code: time several full passes and keep the best.
        var ignored = 0;
        var timings = new List<double>();
        for (var pass = 0; pass < 8; pass++)
        {
            var watch = Stopwatch.StartNew();
            foreach (var path in paths)
            {
                ignored += matcher.IsIgnoredEntry(IgnoreTarget.FileSystem, path) ? 1 : 0;
            }

            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            if (pass < 7)
            {
                Thread.Sleep(50); // gives tiered compilation time to install optimized code
            }
        }

        var best = timings.Min();
        output.WriteLine(
            $"100,000 entry checks against {matcher.RuleCount} rules: best {best:F1} ms, first pass {timings[0]:F1} ms (limit 500 ms; other test projects run at the same time)");
        Assert.Equal(0, ignored);
        Assert.True(best < 500, $"best pass {best:F1} ms");
    }
}
