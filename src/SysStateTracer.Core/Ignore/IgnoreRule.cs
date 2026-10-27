namespace SysStateTracer.Core.Ignore;

/// <summary>What a Global Ignore rule applies to: the <c>FS:</c>, <c>REG:</c> or <c>SVC:</c> prefix.</summary>
public enum IgnoreTarget
{
    FileSystem,
    Registry,
    Service,
}

/// <summary>Where a rule came from. Remote rules (from the remote list, F1) are read-only in Preferences.</summary>
public enum IgnoreRuleSource
{
    Local,
    Remote,
}

/// <summary>
/// One Global Ignore rule. <see cref="Pattern"/> is the normalized path or name that is matched;
/// <see cref="Text"/> is the line as the user wrote it.
/// </summary>
public sealed record IgnoreRule(IgnoreTarget Target, string Pattern, string Text, IgnoreRuleSource Source)
{
    /// <summary>True when the pattern contains <c>*</c> or <c>?</c>.</summary>
    public bool HasWildcards => Pattern.AsSpan().IndexOfAny('*', '?') >= 0;
}

/// <summary>A line that could not be read as a rule.</summary>
public sealed record IgnoreRuleError(int LineNumber, string Line, string Message, IgnoreRuleSource Source);
