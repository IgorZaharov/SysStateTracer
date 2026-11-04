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
        text.Append("Folders:").Append(nl);
        text.Append("  Snapshots:                       ").Append(snapshotFolder).Append(nl);
        text.Append("  Reports, .reg files, Files, log: ").Append(dataFolder).Append(nl);
        text.Append(nl);
        text.Append("Usage:  SysStateTracer.exe [switch] [arguments]").Append(nl);
        text.Append("Without a switch the main window opens.").Append(nl);
        text.Append(nl);
        text.Append("Switches:").Append(nl);
        Item(text, "/?", "Shows this help.");
        Item(text, "/new [\"name\"]", "Takes a snapshot with the scope saved in the New Snapshot window.",
            "Without a name: YYYYMMDD_HH-MM-SS_COMPUTERNAME, e.g. " + Snapshots.SnapshotNaming.DefaultName(new DateTime(2026, 10, 27, 14, 30, 0), "OFFICE-PC") + ".");
        Item(text, "/del [\"name\"]", "Deletes the snapshot; without a name, the newest one.", "Prints which snapshot was deleted.");
        Item(text, "/rename \"old name\" \"new name\"", "Renames a snapshot. Both names are required.");
        Item(text, "/report [\"name1\" \"name2\"] [filters] [/output:\"report.html\"]",
            "Compares two snapshots and writes a self-contained HTML report.",
            "Without names: the newest two. Without a report path: results.html.",
            "A relative report path is placed in the program folder.");
        Item(text, "/export [\"name1\" \"name2\"] [filters] [/output:\"folder\" or \"file.reg\"]",
            "Exports what is new or modified in the newer snapshot:",
            "registry keys and values to Export_<name1>_vs_<name2>.reg, and files to the Files folder",
            "(keeping their folders), with Restore-Files.ps1 there to copy them back to their places.",
            "Without names: the newest two.",
            "/output: a folder (created if needed) gets the .reg file, Restore-Files.ps1 and the Files",
            "folder; a path ending in .reg names the .reg file, with the rest beside it.");
        text.Append(nl);
        text.Append("Filters for /report and /export: only what they name is compared and exported.").Append(nl);
        Item(text, "/registry:\"key\",\"key\"", "Registry keys and everything below them, e.g. \"HKLM\\SOFTWARE\\App\",\"HKCU\\Software\\App\".",
            "HKCU means every user (HKU\\*).");
        Item(text, "/files:\"folder\",\"folder\"", "Folders or files and everything below them, e.g. \"C:\\Program Files\\App\".");
        Item(text, "/services:\"name\",\"name\"", "Services by key name, e.g. \"Spooler\" (/report only).");
        Item(text, "/environment:\"var\",\"var\"", "Environment variables as System\\name or User\\name, e.g. \"System\\Path\" (/report only).");
        text.Append("  Separate several with commas, or repeat the switch (for a path that contains a comma). * and ? work").Append(nl);
        text.Append("  as in the Global Ignore list. With filters, an area without one is left out: /registry: alone exports").Append(nl);
        text.Append("  only a .reg file. A filter that matches nothing gives a warning.").Append(nl);
        text.Append(nl);
        text.Append("The switch names before 1.2.0 still work: /new-snap, /delete-snap, /rename-snap and /compare-snap.").Append(nl);
        text.Append(nl);
        text.Append("A snapshot is named by its file name, with or without .stsnap, in the snapshot folder; or give the path of").Append(nl);
        text.Append("a .stsnap file anywhere. Put names and paths with spaces in quotes.").Append(nl);
        text.Append("Comparisons and exports leave out everything matched by the Global Ignore lists set in Preferences.").Append(nl);
        text.Append(nl);
        text.Append("Exit codes:").Append(nl);
        text.Append("  0 success   1 wrong arguments   2 snapshot not found   4 file could not be read or written").Append(nl);
        text.Append("  5 cancelled (Ctrl+C)").Append(nl);
        text.Append(nl);
        text.Append("Run from a console opened with \"Run as administrator\" for complete snapshots; as a standard user,").Append(nl);
        text.Append("folders and registry keys that need administrator rights are skipped.").Append(nl);
        text.Append(nl);
        text.Append("Examples:").Append(nl);
        Example(text, "Snapshot before and after installing an application:");
        Example(text, "  SysStateTracer.exe /new \"Before Office 2024 install\"");
        Example(text, "  SysStateTracer.exe /new \"After Office 2024 install\"");
        Example(text, "Compare them and save the report in a reports folder:");
        Example(text, "  SysStateTracer.exe /report \"Before Office 2024 install\" \"After Office 2024 install\" \"C:\\Reports\\Office 2024 install changes.html\"");
        Example(text, "Export the new registry values and files the installation added:");
        Example(text, "  SysStateTracer.exe /export \"Before Office 2024 install\" \"After Office 2024 install\"");
        Example(text, "  (gives Export_Before Office 2024 install_vs_After Office 2024 install.reg and Files\\Program Files\\...)");
        Example(text, "Export only the application's registry keys and folders into C:\\Exports\\Office (created if needed):");
        Example(text, "  SysStateTracer.exe /export \"Before Office 2024 install\" \"After Office 2024 install\" ^");
        Example(text, "    /registry:\"HKLM\\SOFTWARE\\Microsoft\\Office\",\"HKCU\\Software\\Microsoft\\Office\" ^");
        Example(text, "    /files:\"C:\\Program Files\\Microsoft Office\",\"C:\\ProgramData\\Microsoft\\Office\" /output:\"C:\\Exports\\Office\"");
        Example(text, "  (gives C:\\Exports\\Office\\Export_...reg, Restore-Files.ps1 and Files\\Program Files\\Microsoft Office\\...)");
        Example(text, "Only the registry, into a .reg file of its own name:");
        Example(text, "  SysStateTracer.exe /export \"Before Office 2024 install\" \"After Office 2024 install\" ^");
        Example(text, "    /registry:\"HKLM\\SOFTWARE\\Microsoft\\Office\" /output:\"C:\\Exports\\Office.reg\"");
        Example(text, "A report of the application's folders, keys and services only:");
        Example(text, "  SysStateTracer.exe /report \"Before Office 2024 install\" \"After Office 2024 install\" ^");
        Example(text, "    /files:\"C:\\Program Files\\Microsoft Office\" /registry:\"HKLM\\SOFTWARE\\Microsoft\\Office\" ^");
        Example(text, "    /services:\"ClickToRunSvc\" /output:\"C:\\Reports\\Office.html\"");
        Example(text, "Snapshot files in another folder, given by path:");
        Example(text, "  SysStateTracer.exe /new \"D:\\Snapshots\\Print server before patch.stsnap\"");
        Example(text, "  SysStateTracer.exe /report \"D:\\Snapshots\\Print server before patch.stsnap\" \"After Office 2024 install.stsnap\"");
        Example(text, "Compare the newest two snapshots into results.html:");
        Example(text, "  SysStateTracer.exe /report");
        Example(text, "Keep a snapshot as a baseline under a clearer name, and delete one that is no longer needed:");
        Example(text, "  SysStateTracer.exe /rename \"After Office 2024 install\" \"Office 2024 baseline\"");
        Example(text, "  SysStateTracer.exe /del \"Before Office 2024 install\"");
        Example(text, "Nightly snapshot from a batch file, named with the date and time (waits for it and checks the exit code):");
        Example(text, "  start \"\" /wait SysStateTracer.exe /new");
        Example(text, "  if errorlevel 1 echo Snapshot failed with code %ERRORLEVEL%");
        Example(text, "From PowerShell, waiting for it and reading the exit code:");
        Example(text, "  $p = Start-Process SysStateTracer.exe '/new \"Before driver update\"' -Wait -PassThru; $p.ExitCode");
        text.Append(nl);
        text.Append("Everything printed is also written to SysStateTracer.log.").Append(nl);
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
