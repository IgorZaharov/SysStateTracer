namespace SysStateTracer.Core.Ignore;

/// <summary>
/// The built-in default Global Ignore list (plan 2.23), embedded in the exe.
/// It becomes the local list file (SysStateTracer.ignore.txt) when that file does not exist yet,
/// where every line can be removed. The remote default (ignore/SysStateTracer.ignore.txt in the
/// repository) starts from the same rules.
/// </summary>
public static class DefaultIgnoreRules
{
    private const string ResourceName = "SysStateTracer.Core.DefaultIgnore.txt";

    private static readonly Lazy<string> TextValue = new(ReadResource);

    /// <summary>The whole file, comments included.</summary>
    public static string Text => TextValue.Value;

    /// <summary>Just the rule lines, trimmed, without comments or blank lines.</summary>
    public static IReadOnlyList<string> RuleLines { get; } = Text
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => !IgnoreRuleParser.IsBlankOrComment(line))
        .ToArray();

    private static string ReadResource()
    {
        using var stream = typeof(DefaultIgnoreRules).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
