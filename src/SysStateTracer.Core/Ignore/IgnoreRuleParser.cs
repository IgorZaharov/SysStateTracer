using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SysStateTracer.Core.Ignore;

/// <summary>
/// Reads Global Ignore rules (syntax: docs/ignore-list.md).
/// <list type="bullet">
/// <item><c>FS:C:\Windows\Temp</c> — a folder or file and everything below it.</item>
/// <item><c>REG:HKLM\SOFTWARE\Vendor</c> — a key and everything below it. HKCU means every user (<c>HKU\*</c>),
/// HKCR means <c>HKLM\SOFTWARE\Classes</c>, and <c>CurrentControlSet</c> matches the real <c>ControlSet00x</c>.</item>
/// <item><c>SVC:edgeupdate</c> — a service by its key name.</item>
/// </list>
/// <c>*</c> matches any text (including across '\'), <c>?</c> one character other than '\'.
/// Matching ignores case. Lines starting with <c>#</c> and blank lines are skipped.
/// </summary>
public static class IgnoreRuleParser
{
    private const string FsPrefix = "FS:";
    private const string RegPrefix = "REG:";
    private const string SvcPrefix = "SVC:";

    /// <summary>Parses a multi-line text such as the default list or a downloaded list.</summary>
    public static (List<IgnoreRule> Rules, List<IgnoreRuleError> Errors) ParseText(string text, IgnoreRuleSource source)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseLines(text.Split('\n'), source);
    }

    /// <summary>Parses lines, keeping every valid rule and reporting the others with their line number.</summary>
    public static (List<IgnoreRule> Rules, List<IgnoreRuleError> Errors) ParseLines(IEnumerable<string> lines, IgnoreRuleSource source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rules = new List<IgnoreRule>();
        var errors = new List<IgnoreRuleError>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            if (IsBlankOrComment(raw))
            {
                continue;
            }

            if (TryParse(raw, source, out var rule, out var error))
            {
                rules.Add(rule);
            }
            else
            {
                errors.Add(new IgnoreRuleError(number, raw.Trim(), error, source));
            }
        }

        return (rules, errors);
    }

    /// <summary>True for empty lines and <c>#</c> comments.</summary>
    public static bool IsBlankOrComment(string? line)
    {
        var trimmed = line.AsSpan().Trim();
        return trimmed.IsEmpty || trimmed[0] == '#';
    }

    /// <summary>Parses one rule line.</summary>
    public static bool TryParse(string line, IgnoreRuleSource source, [NotNullWhen(true)] out IgnoreRule? rule, out string error)
    {
        ArgumentNullException.ThrowIfNull(line);
        rule = null;
        var text = line.Trim();

        IgnoreTarget target;
        string body;
        if (text.StartsWith(FsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            target = IgnoreTarget.FileSystem;
            body = text[FsPrefix.Length..];
        }
        else if (text.StartsWith(RegPrefix, StringComparison.OrdinalIgnoreCase))
        {
            target = IgnoreTarget.Registry;
            body = text[RegPrefix.Length..];
        }
        else if (text.StartsWith(SvcPrefix, StringComparison.OrdinalIgnoreCase))
        {
            target = IgnoreTarget.Service;
            body = text[SvcPrefix.Length..];
        }
        else
        {
            error = "A rule must start with FS:, REG: or SVC:.";
            return false;
        }

        string? pattern;
        if (target == IgnoreTarget.FileSystem)
        {
            pattern = NormalizeFileSystem(body, out error);
        }
        else if (target == IgnoreTarget.Registry)
        {
            pattern = NormalizeRegistry(body, out error);
        }
        else
        {
            pattern = NormalizeService(body, out error);
        }

        if (pattern is null)
        {
            return false;
        }

        rule = new IgnoreRule(target, pattern, text, source);
        error = string.Empty;
        return true;
    }

    /// <summary>The rule text for an item, e.g. when the user picks "Add to Global Ignore list" in the comparison window.</summary>
    public static string Format(IgnoreTarget target, string pathOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrName);
        var prefix = target switch
        {
            IgnoreTarget.FileSystem => FsPrefix,
            IgnoreTarget.Registry => RegPrefix,
            _ => SvcPrefix,
        };

        return prefix + pathOrName.Trim();
    }

    /// <summary>Same normalization the matcher applies to scanned paths: no \\?\ prefix, '\' separators, no trailing '\'.</summary>
    internal static ReadOnlySpan<char> NormalizeInputPath(ReadOnlySpan<char> path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        while (path.Length > 1 && path[^1] == '\\')
        {
            path = path[..^1];
        }

        return path;
    }

    private static string? NormalizeFileSystem(string body, out string error)
    {
        var path = body.Trim().Replace('/', '\\');
        path = NormalizeInputPath(path).ToString();
        return Validate(path, allowBackslash: true, out error);
    }

    private static string? NormalizeRegistry(string body, out string error)
    {
        var path = body.Trim().Replace('/', '\\');
        path = NormalizeInputPath(path).ToString();
        if (Validate(path, allowBackslash: true, out error) is null)
        {
            return null;
        }

        var separator = path.IndexOf('\\', StringComparison.Ordinal);
        var root = separator < 0 ? path : path[..separator];
        var rest = separator < 0 ? string.Empty : path[separator..];

        string? mappedRoot = root.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
            "HKU" or "HKEY_USERS" => "HKU",
            "HKCU" or "HKEY_CURRENT_USER" => @"HKU\*",
            "HKCR" or "HKEY_CLASSES_ROOT" => @"HKLM\SOFTWARE\Classes",
            "HKCC" or "HKEY_CURRENT_CONFIG" => @"HKLM\SYSTEM\CurrentControlSet\Hardware Profiles\Current",
            "*" => "*",
            _ => null,
        };

        if (mappedRoot is null)
        {
            error = "A registry rule must start with HKLM, HKU, HKCU, HKCR or HKCC.";
            return null;
        }

        // The snapshot stores the real ControlSet00x keys, not the CurrentControlSet link.
        var segments = (mappedRoot + rest).Split('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("CurrentControlSet", StringComparison.OrdinalIgnoreCase))
            {
                segments[i] = "ControlSet???";
            }
        }

        error = string.Empty;
        return string.Join('\\', segments);
    }

    private static string? NormalizeService(string body, out string error)
    {
        var name = body.Trim();
        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            error = "A service rule is a service name, without '\\' or '/'.";
            return null;
        }

        return Validate(name, allowBackslash: false, out error);
    }

    private static string? Validate(string pattern, bool allowBackslash, out string error)
    {
        if (pattern.Length == 0 || (allowBackslash && pattern == "\\"))
        {
            error = "The rule has no path or name after its prefix.";
            return null;
        }

        foreach (var c in pattern)
        {
            if (char.IsControl(c))
            {
                error = "The rule contains a control character.";
                return null;
            }
        }

        if (pattern.Replace("*", string.Empty, StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal).Length == 0)
        {
            error = "A rule made only of wildcards would ignore everything.";
            return null;
        }

        error = string.Empty;
        return pattern;
    }

    /// <summary>Rule lines for display or saving, one per line.</summary>
    public static string ToText(IEnumerable<IgnoreRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var builder = new StringBuilder();
        foreach (var rule in rules)
        {
            builder.AppendLine(rule.Text);
        }

        return builder.ToString();
    }
}
