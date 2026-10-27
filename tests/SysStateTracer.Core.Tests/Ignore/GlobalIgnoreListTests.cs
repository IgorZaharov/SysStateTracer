using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Tests.Ignore;

public class GlobalIgnoreListTests
{
    [Fact]
    public void FromText_ParsesRulesAndKeepsErrors()
    {
        var list = GlobalIgnoreList.FromText("# shared list\nREG:HKLM\\SOFTWARE\\Shared\nalso bad\nFS:C:\\Shared\n", IgnoreRuleSource.Remote);

        Assert.Equal(2, list.Rules.Count);
        Assert.All(list.Rules, r => Assert.Equal(IgnoreRuleSource.Remote, r.Source));
        Assert.Single(list.Errors);
        Assert.True(list.Matcher.IsIgnored(IgnoreTarget.FileSystem, @"C:\Shared\x"));
        Assert.True(list.Matcher.IsIgnored(IgnoreTarget.Registry, @"HKLM\SOFTWARE\Shared\x"));
    }

    [Fact]
    public void Fingerprint_IgnoresOrderCaseCommentsAndDuplicates()
    {
        var a = GlobalIgnoreList.Build([@"FS:C:\A", "SVC:x", "# note"]);
        var b = GlobalIgnoreList.Build(["svc:X", @"fs:c:\a\", @"FS:C:\A"]);
        var c = GlobalIgnoreList.Build([@"FS:C:\A"]);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, c.Fingerprint);
    }

    [Fact]
    public void Empty_IgnoresNothing()
    {
        Assert.False(GlobalIgnoreList.Empty.Matcher.IsIgnored(IgnoreTarget.FileSystem, @"C:\Windows"));
        Assert.Equal(0, GlobalIgnoreList.Empty.Matcher.RuleCount);
    }
}
