using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Comparison;

/// <summary>
/// The /files:, /registry: and /services: filters of /report and /export (issue #8): only what they name is
/// kept, with the folders and keys leading to it. A filter is written like a Global Ignore rule without its prefix
/// (wildcards, HKCU = every user, HKCR, CurrentControlSet), and also takes everything below the path it names.
/// When filters are given, an area without a filter is left out.
/// </summary>
public sealed class DiffFilter
{
    private DiffFilter(IReadOnlyList<Entry> entries) => Entries = entries;

    /// <summary>One filter as given, and what it matched (filled by <see cref="Apply"/>).</summary>
    public sealed class Entry
    {
        internal Entry(IgnoreTarget target, string text, IgnoreRule rule)
        {
            Target = target;
            Text = text;
            Rule = rule;
            Matcher = new IgnoreMatcher([rule]);
            var literal = rule.HasWildcards ? rule.Pattern[..rule.Pattern.AsSpan().IndexOfAny('*', '?')] : rule.Pattern;
            LiteralPrefix = rule.HasWildcards ? literal[..(literal.LastIndexOf('\\') + 1)] : literal;
        }

        public IgnoreTarget Target { get; }

        /// <summary>The path or name as given on the command line.</summary>
        public string Text { get; }

        public IgnoreRule Rule { get; }

        /// <summary>Changed items it matched: a new folder or key counts with its contents.</summary>
        public long Matched { get; internal set; }

        /// <summary>
        /// True when the filter points inside a new or deleted folder or key, which a comparison keeps as one item:
        /// that whole folder or key is kept.
        /// </summary>
        public bool WidenedToNewOrDeleted { get; internal set; }

        internal IgnoreMatcher Matcher { get; }

        /// <summary>The part of the pattern before the first wildcard, cut at a backslash.</summary>
        internal string LiteralPrefix { get; }

        /// <summary>True when something this filter names can be at or below <paramref name="path"/>.</summary>
        internal bool MayBeBelow(string path)
        {
            if (Target == IgnoreTarget.Service)
            {
                return false;
            }

            var prefix = LiteralPrefix.TrimEnd('\\');
            return prefix.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals(path, StringComparison.OrdinalIgnoreCase)
                || (Rule.HasWildcards && path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<Entry> Entries { get; }

    public bool Has(IgnoreTarget target) => Entries.Any(e => e.Target == target);

    /// <summary>Reads the filters; false with a message naming the first one that is not valid.</summary>
    public static bool TryCreate(
        IReadOnlyList<string> files, IReadOnlyList<string> registry, IReadOnlyList<string> services, out DiffFilter filter, out string error)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        var entries = new List<Entry>();
        foreach (var (target, values, label) in new[] { (IgnoreTarget.FileSystem, files, "/files:"), (IgnoreTarget.Registry, registry, "/registry:"), (IgnoreTarget.Service, services, "/services:") })
        {
            foreach (var value in values)
            {
                var text = value.Trim();
                if (text.Length == 0)
                {
                    filter = new DiffFilter([]);
                    error = $"{label} has an empty path or name.";
                    return false;
                }

                if (!IgnoreRuleParser.TryParse(IgnoreRuleParser.Format(target, text), IgnoreRuleSource.Local, out var rule, out var why))
                {
                    filter = new DiffFilter([]);
                    error = $"{label}\"{text}\" is not valid: {why}";
                    return false;
                }

                if (!entries.Any(e => e.Target == target && string.Equals(e.Rule.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new Entry(target, text, rule));
                }
            }
        }

        filter = new DiffFilter(entries);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The comparison with only what the filters name; sets each entry's <see cref="Entry.Matched"/>.
    /// Left and right, and a swap, are kept.
    /// </summary>
    public DiffResult Apply(DiffResult diff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        foreach (var entry in Entries)
        {
            entry.Matched = 0;
            entry.WidenedToNewOrDeleted = false;
        }

        return new DiffResult(
            diff.Left,
            diff.Right,
            Filter(diff, diff.FileSystem, IgnoreTarget.FileSystem, cancellationToken),
            Filter(diff, diff.Registry, IgnoreTarget.Registry, cancellationToken),
            Filter(diff, diff.Services, IgnoreTarget.Service, cancellationToken),
            diff.Ignore);
    }

    private DiffTree Filter(DiffResult diff, DiffTree tree, IgnoreTarget target, CancellationToken cancellationToken)
    {
        var entries = Entries.Where(e => e.Target == target).ToArray();
        if (entries.Length == 0)
        {
            return tree.IsSwapped ? DiffTree.Empty.Swap() : DiffTree.Empty;
        }

        var builder = new DiffTreeBuilder();
        foreach (var root in tree.Roots())
        {
            Visit(root, -1);
        }

        var result = builder.Build();
        return tree.IsSwapped ? result.Swap() : result;

        void Visit(int node, int parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = IgnoreRuleParser.NormalizeInputPath(diff.FullPath(tree, node)).ToString().TrimEnd('\\');
            var hit = Array.Find(entries, e => e.Matcher.IsIgnored(target, path));
            if (hit is not null)
            {
                hit.Matched += tree.ChangedItems(node);
                Copy(node, parent);
                return;
            }

            var below = entries.Where(e => e.MayBeBelow(path)).ToList();
            if (below.Count == 0)
            {
                return;
            }

            if (tree.ChangeFromOlder(node) is ChangeKind.New or ChangeKind.Deleted)
            {
                // What it holds is not in the comparison as items of its own: keep the whole folder or key.
                below[0].Matched += tree.ChangedItems(node);
                below.ForEach(e => e.WidenedToNewOrDeleted = true);
                Copy(node, parent);
                return;
            }

            // A folder or key on the way to a filter: shown only because something below it is kept.
            var kept = builder.Add(parent, tree.Kind(node), ChangeKind.Unchanged, ChangedFields.None, tree.OlderIndex(node), tree.NewerIndex(node), 0);
            foreach (var child in tree.Children(node))
            {
                Visit(child, kept);
            }

            builder.Close(kept);
        }

        void Copy(int node, int parent)
        {
            var own = tree.ChangedItems(node) - tree.Children(node).Sum(tree.ChangedItems);
            var kept = builder.Add(parent, tree.Kind(node), tree.ChangeFromOlder(node), tree.Fields(node), tree.OlderIndex(node), tree.NewerIndex(node), own);
            foreach (var child in tree.Children(node))
            {
                Copy(child, kept);
            }

            builder.Close(kept);
        }
    }
}
