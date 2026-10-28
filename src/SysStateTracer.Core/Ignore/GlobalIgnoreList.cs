using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace SysStateTracer.Core.Ignore;

/// <summary>
/// The Global Ignore list in effect (the local file or the remote list, decision F1),
/// parsed and compiled into one <see cref="IgnoreMatcher"/>.
/// </summary>
public sealed class GlobalIgnoreList
{
    private GlobalIgnoreList(IReadOnlyList<IgnoreRule> rules, IReadOnlyList<IgnoreRuleError> errors)
    {
        Rules = rules;
        Errors = errors;
        Matcher = new IgnoreMatcher(rules);
        Fingerprint = ComputeFingerprint(rules);
    }

    public static GlobalIgnoreList Empty { get; } = new([], []);

    public IReadOnlyList<IgnoreRule> Rules { get; }

    /// <summary>Lines that are not valid rules; shown in Preferences so they can be fixed.</summary>
    public IReadOnlyList<IgnoreRuleError> Errors { get; }

    public IgnoreMatcher Matcher { get; }

    /// <summary>
    /// Hash of the effective rules, independent of order, comments and duplicates.
    /// Stored with a snapshot's cache so a changed ignore list forces a fresh scan (plan §7.8).
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>Rules from individual lines (blank lines and comments are skipped).</summary>
    public static GlobalIgnoreList Build(IEnumerable<string> lines, IgnoreRuleSource source = IgnoreRuleSource.Local)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var (rules, errors) = IgnoreRuleParser.ParseLines(lines, source);
        return new GlobalIgnoreList(rules, errors);
    }

    /// <summary>The rules of both lists (F22: the local and the remote list apply together).</summary>
    public static GlobalIgnoreList Combine(GlobalIgnoreList first, GlobalIgnoreList second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return new GlobalIgnoreList([.. first.Rules, .. second.Rules], [.. first.Errors, .. second.Errors]);
    }

    /// <summary>Rules from the text of a list file.</summary>
    public static GlobalIgnoreList FromText(string text, IgnoreRuleSource source)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (rules, errors) = IgnoreRuleParser.ParseText(text, source);
        return new GlobalIgnoreList(rules, errors);
    }

    private static string ComputeFingerprint(IReadOnlyList<IgnoreRule> rules)
    {
        var keys = rules
            .Select(r => $"{(int)r.Target}|{r.Pattern.ToUpperInvariant()}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', keys));
        return XxHash3.HashToUInt64(bytes).ToString("x16", CultureInfo.InvariantCulture);
    }
}
