namespace SysStateTracer.Core.Ignore;

/// <summary>
/// Compiled Global Ignore rules. Rules without wildcards go into hash sets (one lookup per check);
/// wildcard rules are pre-filtered by their longest literal part before the glob match runs.
/// Thread-safe for concurrent reads once built.
/// </summary>
public sealed class IgnoreMatcher
{
    private readonly Target[] _targets;

    public IgnoreMatcher(IEnumerable<IgnoreRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _targets = [new Target(), new Target(), new Target()];
        var count = 0;
        foreach (var rule in rules)
        {
            if (_targets[(int)rule.Target].Add(rule.Pattern, rule.HasWildcards))
            {
                count++;
            }
        }

        RuleCount = count;
    }

    public static IgnoreMatcher Empty { get; } = new([]);

    /// <summary>Number of distinct rules.</summary>
    public int RuleCount { get; }

    /// <summary>
    /// Fast check used while scanning: true when a rule names this entry itself.
    /// Assumes the entry's parent is not ignored (scanners never descend into an ignored folder),
    /// which lets most paths be rejected by looking only at their last name.
    /// </summary>
    public bool IsIgnoredEntry(IgnoreTarget target, ReadOnlySpan<char> path)
    {
        var t = _targets[(int)target];
        if (t.IsEmpty)
        {
            return false;
        }

        if (target != IgnoreTarget.Service)
        {
            path = IgnoreRuleParser.NormalizeInputPath(path);
        }

        return t.EntryMatches(path);
    }

    /// <summary>
    /// Full check used for comparison views and exports: true when a rule names this path or any
    /// folder/key above it. Services have no hierarchy, so this equals <see cref="IsIgnoredEntry"/> for them.
    /// </summary>
    public bool IsIgnored(IgnoreTarget target, ReadOnlySpan<char> path)
    {
        if (target == IgnoreTarget.Service)
        {
            return IsIgnoredEntry(target, path);
        }

        var t = _targets[(int)target];
        if (t.IsEmpty)
        {
            return false;
        }

        path = IgnoreRuleParser.NormalizeInputPath(path);
        if (t.HasExact)
        {
            for (var end = 0; end <= path.Length; end++)
            {
                if ((end == path.Length || path[end] == '\\') && end > 0 && t.ExactContains(path[..end]))
                {
                    return true;
                }
            }
        }

        return t.WildcardMatches(path, subtree: true);
    }

    /// <summary>
    /// Case-insensitive glob: <c>*</c> matches any text including '\', <c>?</c> one character other than '\'.
    /// </summary>
    internal static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length &&
                     (pattern[p] == '?' ? text[t] != '\\' : char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private sealed class Target
    {
        private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _exactLookup;

        // Last names of exact rules: most entries are rejected by this one short lookup.
        private readonly HashSet<string> _exactNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _exactNameLookup;

        // Wildcard rules whose last name is literal ("*\AppData\Local\Temp" -> "Temp").
        private readonly Dictionary<string, List<WildcardRule>> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<WildcardRule>>.AlternateLookup<ReadOnlySpan<char>> _byNameLookup;

        // Wildcard rules whose last name has '*' only at its start or end ("*.blf", "NTUSER.DAT*").
        private readonly List<WildcardRule> _nameFiltered = [];

        // Everything else: pre-filtered by the longest literal part.
        private readonly List<WildcardRule> _general = [];

        private readonly List<WildcardRule> _allWildcards = [];

        public Target()
        {
            _exactLookup = _exact.GetAlternateLookup<ReadOnlySpan<char>>();
            _exactNameLookup = _exactNames.GetAlternateLookup<ReadOnlySpan<char>>();
            _byNameLookup = _byName.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public bool IsEmpty => _exact.Count == 0 && _allWildcards.Count == 0;

        public bool HasExact => _exact.Count > 0;

        public bool Add(string pattern, bool hasWildcards)
        {
            if (!hasWildcards)
            {
                if (!_exact.Add(pattern))
                {
                    return false;
                }

                _exactNames.Add(LastName(pattern).ToString());
                return true;
            }

            foreach (var existing in _allWildcards)
            {
                if (string.Equals(existing.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var rule = new WildcardRule(pattern);
            _allWildcards.Add(rule);
            var last = LastName(pattern).ToString();
            if (last.AsSpan().IndexOfAny('*', '?') < 0)
            {
                if (!_byName.TryGetValue(last, out var list))
                {
                    list = [];
                    _byName[last] = list;
                }

                list.Add(rule);
            }
            else if (last.AsSpan().Trim('*').IndexOf('*') < 0)
            {
                rule.NameFilter = last;
                _nameFiltered.Add(rule);
            }
            else
            {
                _general.Add(rule);
            }

            return true;
        }

        public bool ExactContains(ReadOnlySpan<char> path) => _exact.Count > 0 && _exactLookup.Contains(path);

        public bool EntryMatches(ReadOnlySpan<char> path)
        {
            var name = LastName(path);

            if (_exactNames.Count > 0 && _exactNameLookup.Contains(name) && _exactLookup.Contains(path))
            {
                return true;
            }

            if (_byName.Count > 0 && _byNameLookup.TryGetValue(name, out var candidates))
            {
                foreach (var rule in candidates)
                {
                    if (Glob(rule.Pattern, path))
                    {
                        return true;
                    }
                }
            }

            foreach (var rule in _nameFiltered)
            {
                if (Glob(rule.NameFilter!, name) && Glob(rule.Pattern, path))
                {
                    return true;
                }
            }

            foreach (var rule in _general)
            {
                if ((rule.Literal.Length == 0 || path.Contains(rule.Literal, StringComparison.OrdinalIgnoreCase)) &&
                    Glob(rule.Pattern, path))
                {
                    return true;
                }
            }

            return false;
        }

        public bool WildcardMatches(ReadOnlySpan<char> path, bool subtree)
        {
            foreach (var rule in _allWildcards)
            {
                if (rule.Literal.Length > 0 && !path.Contains(rule.Literal, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Glob(rule.Pattern, path) || (subtree && Glob(rule.SubtreePattern, path)))
                {
                    return true;
                }
            }

            return false;
        }

        private static ReadOnlySpan<char> LastName(ReadOnlySpan<char> path)
        {
            var slash = path.LastIndexOf('\\');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }

    private sealed class WildcardRule
    {
        public WildcardRule(string pattern)
        {
            Pattern = pattern;
            SubtreePattern = pattern + @"\*";
            Literal = LongestLiteral(pattern);
        }

        public string Pattern { get; }

        /// <summary>Pattern + "\*": matches anything below a matching folder or key.</summary>
        public string SubtreePattern { get; }

        /// <summary>Longest wildcard-free part; a path without it cannot match.</summary>
        public string Literal { get; }

        /// <summary>The pattern's last name when it can be checked against an entry's name on its own.</summary>
        public string? NameFilter { get; set; }

        private static string LongestLiteral(string pattern)
        {
            var best = string.Empty;
            foreach (var part in pattern.Split('*', '?'))
            {
                if (part.Length > best.Length)
                {
                    best = part;
                }
            }

            return best;
        }
    }
}
