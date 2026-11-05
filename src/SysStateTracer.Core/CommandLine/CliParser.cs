namespace SysStateTracer.Core.CommandLine;

/// <summary>What the command line asks for (plan §10.9).</summary>
public enum CliCommandKind
{
    Help,
    NewSnap,
    DeleteSnap,
    RenameSnap,
    CompareSnap,
    Export,
}

/// <summary>Process exit codes (plan §10.9).</summary>
public enum CliExitCode
{
    Success = 0,
    BadArguments = 1,
    SnapshotNotFound = 2,
    /// <summary>No longer used: since 1.0.0 a standard user can run every switch (F9).</summary>
    NotElevated = 3,
    ReadWriteError = 4,
    Cancelled = 5,
}

/// <summary>
/// A parsed command line: the switch, snapshot names, for /report the report path, and for /report and
/// /export the filters and /output: (issue #8).
/// </summary>
public sealed record CliCommand(CliCommandKind Kind, IReadOnlyList<string> Names, string? ReportPath = null)
{
    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<string> Registry { get; init; } = [];

    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>/environment: "System\Path", "User\*" (/report only).</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    /// <summary>/output: as given: the .html report, or for /export a folder or a .reg file.</summary>
    public string? OutputPath { get; init; }

    /// <summary>/permissions with /new: record permissions this time even when New Snapshot leaves them out.</summary>
    public bool Permissions { get; init; }

    public bool HasFilters => Files.Count + Registry.Count + Services.Count + Environment.Count > 0;
}

/// <summary>Either a command or the reason the arguments are wrong.</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public static CliParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Parses the switches of plan §10.9. Switches are case-insensitive and start with "/" or "-";
/// one switch per call. For /report, an argument ending in .html is the report path (plan 2.11).
/// </summary>
public static class CliParser
{
    public const string HtmlExtension = ".html";

    private static readonly Dictionary<string, CliCommandKind> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["?"] = CliCommandKind.Help,
        ["h"] = CliCommandKind.Help,
        ["help"] = CliCommandKind.Help,
        ["-help"] = CliCommandKind.Help,
        ["new"] = CliCommandKind.NewSnap,
        ["del"] = CliCommandKind.DeleteSnap,
        ["rename"] = CliCommandKind.RenameSnap,
        ["report"] = CliCommandKind.CompareSnap,
        ["export"] = CliCommandKind.Export,

        // The names before 1.2.0, still accepted so existing scripts keep working.
        ["new-snap"] = CliCommandKind.NewSnap,
        ["delete-snap"] = CliCommandKind.DeleteSnap,
        ["rename-snap"] = CliCommandKind.RenameSnap,
        ["compare-snap"] = CliCommandKind.CompareSnap,
    };

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return CliParseResult.Fail("No switch given.");
        }

        if (!TryGetSwitch(args[0], out var kind))
        {
            return CliParseResult.Fail(IsSwitchLike(args[0])
                ? $"Unknown switch {args[0]}."
                : $"Expected a switch such as /new, not \"{args[0]}\".");
        }

        var rest = args.Skip(1).ToList();
        var permissions = rest.RemoveAll(IsPermissionsFlag) > 0;
        if (permissions && kind != CliCommandKind.NewSnap)
        {
            return CliParseResult.Fail("/permissions works only with /new.");
        }

        var options = rest.Where(IsOption).ToList();
        rest = [.. rest.Where(a => !IsOption(a))];
        var files = new List<string>();
        var registry = new List<string>();
        var services = new List<string>();
        var environment = new List<string>();
        string? output = null;
        foreach (var option in options)
        {
            var colon = option.IndexOf(':', StringComparison.Ordinal);
            var key = option[..colon].TrimStart('/', '-');
            var value = option[(colon + 1)..].Trim().Trim('"');
            if (kind is not (CliCommandKind.CompareSnap or CliCommandKind.Export))
            {
                return CliParseResult.Fail($"{option[..(colon + 1)]} works only with /report and /export.");
            }

            if (value.Length == 0)
            {
                return CliParseResult.Fail($"{option[..(colon + 1)]} needs a value, e.g. {Example(key)}.");
            }

            switch (key.ToLowerInvariant())
            {
                case "files":
                    files.AddRange(SplitList(value));
                    break;
                case "registry":
                    registry.AddRange(SplitList(value));
                    break;
                case "services":
                    services.AddRange(SplitList(value));
                    break;
                case "environment":
                    environment.AddRange(SplitList(value));
                    break;
                case "output":
                    if (output is not null)
                    {
                        return CliParseResult.Fail("Give /output: once.");
                    }

                    output = value;
                    break;
                default:
                    return CliParseResult.Fail($"Unknown option {option[..(colon + 1)]}; use /files:, /registry:, /services:, /environment: or /output:.");
            }
        }

        if (files.Concat(registry).Concat(services).Concat(environment).Any(string.IsNullOrWhiteSpace))
        {
            return CliParseResult.Fail("A /files:, /registry:, /services: or /environment: list has an empty entry (two commas in a row, or a comma at the end).");
        }

        var extra = rest.FirstOrDefault(a => TryGetSwitch(a, out _));
        if (extra is not null)
        {
            return CliParseResult.Fail($"Use one switch at a time ({args[0]} and {extra}).");
        }

        if (rest.Any(string.IsNullOrWhiteSpace))
        {
            return CliParseResult.Fail("A snapshot name or path is empty.");
        }

        var name = SwitchName(kind);
        switch (kind)
        {
            case CliCommandKind.Help:
                return new CliParseResult(new CliCommand(kind, []), null);

            case CliCommandKind.NewSnap or CliCommandKind.DeleteSnap:
                return rest.Count <= 1
                    ? new CliParseResult(new CliCommand(kind, rest) { Permissions = permissions }, null)
                    : CliParseResult.Fail($"{name} takes at most one snapshot name; put names with spaces in quotes.");

            case CliCommandKind.RenameSnap:
                return rest.Count == 2
                    ? new CliParseResult(new CliCommand(kind, rest), null)
                    : CliParseResult.Fail($"{name} needs the current and the new name: {name} \"old name\" \"new name\".");

            case CliCommandKind.CompareSnap:
            {
                var reports = rest.Where(a => a.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase)).ToList();
                var names = rest.Where(a => !a.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase)).ToList();
                if (output is not null)
                {
                    if (!output.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        return CliParseResult.Fail($"{name} writes an HTML report: /output: must end in .html, not \"{output}\".");
                    }

                    reports.Add(output);
                }

                if (reports.Count > 1)
                {
                    return CliParseResult.Fail($"{name} writes one report; {reports.Count} .html paths were given.");
                }

                return names.Count is 0 or 2
                    ? new CliParseResult(new CliCommand(kind, names, reports.FirstOrDefault()) { Files = files, Registry = registry, Services = services, Environment = environment, OutputPath = output }, null)
                    : CliParseResult.Fail($"{name} needs two snapshot names, or none for the newest two.");
            }

            default:
                if (services.Count > 0)
                {
                    return CliParseResult.Fail($"{name} exports registry keys and files, not services: /services: works only with /report.");
                }

                if (environment.Count > 0)
                {
                    return CliParseResult.Fail($"{name} exports registry keys and files, not environment variables: /environment: works only with /report.");
                }

                if (output is not null && output.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return CliParseResult.Fail($"{name} writes a .reg file and copies files: /output: is a folder or a .reg file, not \"{output}\". Use /report for an HTML report.");
                }

                return rest.Count is 0 or 2
                    ? new CliParseResult(new CliCommand(kind, rest) { Files = files, Registry = registry, Services = services, OutputPath = output }, null)
                    : CliParseResult.Fail($"{name} needs two snapshot names, or none for the newest two.");
        }
    }

    /// <summary>
    /// "/files:…", "-registry:…", "--output:…": a word and a colon after one or two dashes or a slash. A snapshot path
    /// such as C:\a or /x/y has no colon right after a word, so it is never taken for an option.
    /// </summary>
    private static bool IsPermissionsFlag(string arg) =>
        arg.TrimStart('/', '-').Equals("permissions", StringComparison.OrdinalIgnoreCase) && arg.Length > "permissions".Length;

    private static bool IsOption(string arg)
    {
        var start = arg.StartsWith("--", StringComparison.Ordinal) ? 2 : arg.Length > 0 && arg[0] is '/' or '-' ? 1 : 0;
        if (start == 0)
        {
            return false;
        }

        var colon = arg.IndexOf(':', StringComparison.Ordinal);
        return colon > start && arg[start..colon].All(c => char.IsAsciiLetter(c) || c == '-');
    }

    /// <summary>"HKCU","HKLM\Software" arrives as HKCU,HKLM\Software: split at commas, quotes removed.</summary>
    private static IEnumerable<string> SplitList(string value) => value.Split(',').Select(v => v.Trim().Trim('"').Trim());

    private static string Example(string key) => key.ToLowerInvariant() switch
    {
        "files" => "/files:\"C:\\Program Files\\App\"",
        "registry" => "/registry:\"HKLM\\SOFTWARE\\App\"",
        "services" => "/services:\"Spooler\"",
        "environment" => "/environment:\"System\\Path\"",
        _ => "/output:\"C:\\Exports\"",
    };

    /// <summary>"/new" for <see cref="CliCommandKind.NewSnap"/>.</summary>
    public static string SwitchName(CliCommandKind kind) => kind switch
    {
        CliCommandKind.Help => "/?",
        CliCommandKind.NewSnap => "/new",
        CliCommandKind.DeleteSnap => "/del",
        CliCommandKind.RenameSnap => "/rename",
        CliCommandKind.CompareSnap => "/report",
        _ => "/export",
    };

    private static bool IsSwitchLike(string arg) => arg.Length > 1 && arg[0] is '/' or '-' && !arg.Contains('\\', StringComparison.Ordinal);

    private static bool TryGetSwitch(string arg, out CliCommandKind kind)
    {
        kind = default;
        return IsSwitchLike(arg) && Switches.TryGetValue(arg[1..], out kind);
    }
}
