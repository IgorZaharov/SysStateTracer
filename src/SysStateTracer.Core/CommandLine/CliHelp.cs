using System.Text;

namespace SysStateTracer.Core.CommandLine;

/// <summary>The text printed by /? (plan §10.9): every switch, where files go, exit codes and examples.</summary>
public static class CliHelp
{
    /// <param name="snapshotFolder">Where snapshots are kept (Preferences, or beside the exe).</param>
    /// <param name="dataFolder">Where reports, .reg exports and Files\ go (beside the exe).</param>
    public static string Text(string snapshotFolder, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(snapshotFolder);
        ArgumentNullException.ThrowIfNull(dataFolder);
        var nl = Environment.NewLine;
        var text = new StringBuilder();
        text.Append(AppInfo.Name).Append(' ').Append(AppInfo.Version).Append(nl);
        text.Append("Takes snapshots of folders, files, registry and services, and shows what changed between two of them.").Append(nl);
        text.Append(nl);
        text.Append("Usage:  SysStateTracer.exe [switch] [arguments]").Append(nl);
        text.Append("Without a switch the main window opens.").Append(nl);
        text.Append(nl);
        text.Append("Switches:").Append(nl);
        Item(text, "/?", "Shows this help.");
        Item(text, "/new-snap [\"name\"]", "Takes a snapshot with the scope saved in the New Snapshot window.",
            "Without a name: YYYYMMDD_HH-MM-SS_COMPUTERNAME, e.g. " + Snapshots.SnapshotNaming.DefaultName(new DateTime(2026, 10, 27, 14, 30, 0), "OFFICE-PC") + ".");
        Item(text, "/delete-snap [\"name\"]", "Deletes the snapshot; without a name, the newest one.", "Prints which snapshot was deleted.");
        Item(text, "/rename-snap \"old name\" \"new name\"", "Renames a snapshot. Both names are required.");
        Item(text, "/compare-snap [\"name1\" \"name2\"] [\"report.html\"]",
            "Compares two snapshots and writes a self-contained HTML report.",
            "Without names: the newest two. Without a report path: results.html.",
            "A relative report path is placed in the program folder.");
        Item(text, "/export [\"name1\" \"name2\"]",
            "Exports what is new or modified in the newer snapshot:",
            "registry keys and values to Export_<name1>_vs_<name2>.reg, and files to the Files folder",
            "(keeping their folders). Without names: the newest two.");
        text.Append(nl);
        text.Append("Names are snapshot file names without .stsnap. Put names with spaces in quotes.").Append(nl);
        text.Append("Comparisons and exports leave out everything matched by the Global Ignore list chosen in Preferences.").Append(nl);
        text.Append(nl);
        text.Append("Folders:").Append(nl);
        text.Append("  Snapshots:                  ").Append(snapshotFolder).Append(nl);
        text.Append("  Reports, .reg files, Files: ").Append(dataFolder).Append(nl);
        text.Append(nl);
        text.Append("Exit codes:").Append(nl);
        text.Append("  0 success   1 wrong arguments   2 snapshot not found   3 administrator rights needed").Append(nl);
        text.Append("  4 file could not be read or written   5 cancelled (Ctrl+C)").Append(nl);
        text.Append(nl);
        text.Append("Examples:").Append(nl);
        Example(text, "Snapshot before and after installing an application:");
        Example(text, "  SysStateTracer.exe /new-snap \"Before Office 2024 install\"");
        Example(text, "  SysStateTracer.exe /new-snap \"After Office 2024 install\"");
        Example(text, "Compare them and save the report in a reports folder:");
        Example(text, "  SysStateTracer.exe /compare-snap \"Before Office 2024 install\" \"After Office 2024 install\" \"C:\\Reports\\Office 2024 install changes.html\"");
        Example(text, "Export the new registry values and files the installation added:");
        Example(text, "  SysStateTracer.exe /export \"Before Office 2024 install\" \"After Office 2024 install\"");
        Example(text, "  (gives Export_Before Office 2024 install_vs_After Office 2024 install.reg and Files\\Program Files\\...)");
        Example(text, "Compare the newest two snapshots into results.html:");
        Example(text, "  SysStateTracer.exe /compare-snap");
        Example(text, "Keep a snapshot as a baseline under a clearer name, and delete one that is no longer needed:");
        Example(text, "  SysStateTracer.exe /rename-snap \"After Office 2024 install\" \"Office 2024 baseline\"");
        Example(text, "  SysStateTracer.exe /delete-snap \"Before Office 2024 install\"");
        Example(text, "Nightly snapshot from a batch file, named with the date and time (waits for it and checks the exit code):");
        Example(text, "  start \"\" /wait SysStateTracer.exe /new-snap");
        Example(text, "  if errorlevel 1 echo Snapshot failed with code %ERRORLEVEL%");
        Example(text, "The same from PowerShell:");
        Example(text, "  $p = Start-Process SysStateTracer.exe '/new-snap \"Before driver update\"' -Wait -PassThru; $p.ExitCode");
        text.Append(nl);
        text.Append("Output is also written to SysStateTracer.log in ").Append(dataFolder).Append('.').Append(nl);
        text.Append("Documentation and source: ").Append(AppInfo.GitHubUrl).Append(nl);
        return text.ToString();

        void Example(StringBuilder b, string line) => b.Append("  ").Append(line).Append(nl);
    }

    private static void Item(StringBuilder text, string usage, params string[] lines)
    {
        const int Column = 38;
        var nl = Environment.NewLine;
        text.Append("  ").Append(usage);
        if (usage.Length + 2 >= Column)
        {
            text.Append(nl).Append(' ', Column);
        }
        else
        {
            text.Append(' ', Column - usage.Length - 2);
        }

        text.Append(lines[0]).Append(nl);
        foreach (var line in lines.Skip(1))
        {
            text.Append(' ', Column).Append(line).Append(nl);
        }
    }
}
