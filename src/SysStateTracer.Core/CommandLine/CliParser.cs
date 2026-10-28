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

/// <summary>A parsed command line: the switch, snapshot names, and for /compare-snap the report path.</summary>
public sealed record CliCommand(CliCommandKind Kind, IReadOnlyList<string> Names, string? ReportPath = null);

/// <summary>Either a command or the reason the arguments are wrong.</summary>
public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public static CliParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Parses the switches of plan §10.9. Switches are case-insensitive and start with "/" or "-";
/// one switch per call. For /compare-snap, an argument ending in .html is the report path (plan 2.11).
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
        ["new-snap"] = CliCommandKind.NewSnap,
        ["delete-snap"] = CliCommandKind.DeleteSnap,
        ["rename-snap"] = CliCommandKind.RenameSnap,
        ["compare-snap"] = CliCommandKind.CompareSnap,
        ["export"] = CliCommandKind.Export,
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
                : $"Expected a switch such as /new-snap, not \"{args[0]}\".");
        }

        var rest = args.Skip(1).ToList();
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
                    ? new CliParseResult(new CliCommand(kind, rest), null)
                    : CliParseResult.Fail($"{name} takes at most one snapshot name; put names with spaces in quotes.");

            case CliCommandKind.RenameSnap:
                return rest.Count == 2
                    ? new CliParseResult(new CliCommand(kind, rest), null)
                    : CliParseResult.Fail($"{name} needs the current and the new name: {name} \"old name\" \"new name\".");

            case CliCommandKind.CompareSnap:
            {
                var reports = rest.Where(a => a.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase)).ToList();
                var names = rest.Where(a => !a.EndsWith(HtmlExtension, StringComparison.OrdinalIgnoreCase)).ToList();
                if (reports.Count > 1)
                {
                    return CliParseResult.Fail($"{name} writes one report; {reports.Count} .html paths were given.");
                }

                return names.Count is 0 or 2
                    ? new CliParseResult(new CliCommand(kind, names, reports.FirstOrDefault()), null)
                    : CliParseResult.Fail($"{name} needs two snapshot names, or none for the newest two.");
            }

            default:
                return rest.Count is 0 or 2
                    ? new CliParseResult(new CliCommand(kind, rest), null)
                    : CliParseResult.Fail($"{name} needs two snapshot names, or none for the newest two.");
        }
    }

    /// <summary>"/new-snap" for <see cref="CliCommandKind.NewSnap"/>.</summary>
    public static string SwitchName(CliCommandKind kind) => kind switch
    {
        CliCommandKind.Help => "/?",
        CliCommandKind.NewSnap => "/new-snap",
        CliCommandKind.DeleteSnap => "/delete-snap",
        CliCommandKind.RenameSnap => "/rename-snap",
        CliCommandKind.CompareSnap => "/compare-snap",
        _ => "/export",
    };

    private static bool IsSwitchLike(string arg) => arg.Length > 1 && arg[0] is '/' or '-' && !arg.Contains('\\', StringComparison.Ordinal);

    private static bool TryGetSwitch(string arg, out CliCommandKind kind)
    {
        kind = default;
        return IsSwitchLike(arg) && Switches.TryGetValue(arg[1..], out kind);
    }
}
