using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Tests.Ignore;

public class IgnoreRuleParserTests
{
    [Theory]
    [InlineData(@"FS:C:\Windows\Temp", IgnoreTarget.FileSystem, @"C:\Windows\Temp")]
    [InlineData(@"fs:C:\Windows\Temp\", IgnoreTarget.FileSystem, @"C:\Windows\Temp")]
    [InlineData(@"  FS: C:/Data/Logs/  ", IgnoreTarget.FileSystem, @"C:\Data\Logs")]
    [InlineData(@"FS:\\?\C:\Big", IgnoreTarget.FileSystem, @"C:\Big")]
    [InlineData(@"FS:C:\", IgnoreTarget.FileSystem, "C:")]
    [InlineData(@"REG:HKLM\SOFTWARE\Vendor", IgnoreTarget.Registry, @"HKLM\SOFTWARE\Vendor")]
    [InlineData(@"REG:HKEY_LOCAL_MACHINE\SOFTWARE\Vendor", IgnoreTarget.Registry, @"HKLM\SOFTWARE\Vendor")]
    [InlineData(@"REG:hkey_users\.DEFAULT", IgnoreTarget.Registry, @"HKU\.DEFAULT")]
    [InlineData(@"REG:HKCU\Software\Test", IgnoreTarget.Registry, @"HKU\*\Software\Test")]
    [InlineData(@"REG:HKCR\.txt", IgnoreTarget.Registry, @"HKLM\SOFTWARE\Classes\.txt")]
    [InlineData(@"REG:HKLM\SYSTEM\CurrentControlSet\Services\bam", IgnoreTarget.Registry, @"HKLM\SYSTEM\ControlSet???\Services\bam")]
    [InlineData("SVC:edgeupdate", IgnoreTarget.Service, "edgeupdate")]
    [InlineData("svc: Spooler ", IgnoreTarget.Service, "Spooler")]
    public void TryParse_NormalizesValidRules(string line, IgnoreTarget target, string pattern)
    {
        Assert.True(IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out var rule, out var error), error);
        Assert.Equal(target, rule.Target);
        Assert.Equal(pattern, rule.Pattern);
        Assert.Equal(line.Trim(), rule.Text);
        Assert.Equal(IgnoreRuleSource.Local, rule.Source);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp", "must start with")]
    [InlineData("FS:", "no path")]
    [InlineData("FS:   ", "no path")]
    [InlineData(@"FS:\", "no path")]
    [InlineData("FS:*", "only of wildcards")]
    [InlineData(@"FS:*\*", "only of wildcards")]
    [InlineData(@"REG:HKXX\Foo", "must start with HKLM")]
    [InlineData(@"SVC:a\b", "without")]
    [InlineData("SVC:", "no path")]
    public void TryParse_RejectsInvalidRules(string line, string messagePart)
    {
        Assert.False(IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out _, out var error));
        Assert.Contains(messagePart, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseText_SkipsCommentsAndBlankLines_AndReportsLineNumbers()
    {
        var text = "# comment\r\n\r\nFS:C:\\Temp\r\nnonsense\r\n   # indented comment\r\nSVC:BITS\r\n";

        var (rules, errors) = IgnoreRuleParser.ParseText(text, IgnoreRuleSource.Remote);

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(IgnoreRuleSource.Remote, r.Source));
        var error = Assert.Single(errors);
        Assert.Equal(4, error.LineNumber);
        Assert.Equal("nonsense", error.Line);
    }

    [Theory]
    [InlineData(IgnoreTarget.FileSystem, @"C:\Program Files\App", @"FS:C:\Program Files\App")]
    [InlineData(IgnoreTarget.Registry, @"HKLM\SOFTWARE\App", @"REG:HKLM\SOFTWARE\App")]
    [InlineData(IgnoreTarget.Service, "AppSvc", "SVC:AppSvc")]
    public void Format_ProducesParsableRule(IgnoreTarget target, string path, string expected)
    {
        var line = IgnoreRuleParser.Format(target, path);

        Assert.Equal(expected, line);
        Assert.True(IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out _, out _));
    }

    [Fact]
    public void DefaultRules_AllParse()
    {
        var (rules, errors) = IgnoreRuleParser.ParseText(DefaultIgnoreRules.Text, IgnoreRuleSource.Local);

        Assert.Empty(errors);
        Assert.True(rules.Count >= 30, $"{rules.Count} default rules");
        Assert.Equal(rules.Count, DefaultIgnoreRules.RuleLines.Count);
        Assert.Contains(rules, r => r.Pattern == @"HKLM\SAM");
        Assert.Contains(rules, r => r.Pattern == @"HKLM\SECURITY");
    }
}
