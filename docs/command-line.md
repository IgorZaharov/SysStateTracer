# Command line

`SysStateTracer.exe` with a switch runs one task and exits; without a switch the main window opens.
It runs with the rights of the console that starts it (F9). From a console opened with **Run as administrator**
snapshots are complete; from a normal console, folders and registry keys that need administrator rights are
skipped (a note says so) and whole drives are read more slowly. Everything printed also goes to `SysStateTracer.log`.

A window application does not hold up an interactive `cmd` prompt, so the prompt can come back before the
output. In batch files use `start "" /wait`, in PowerShell `Start-Process … -Wait`, to wait for the result
and its exit code.

## `SysStateTracer.exe /?`

```text
SysStateTracer 0.1.0+1a2b3c4
Takes snapshots of folders, files, registry and services, and shows what changed between two of them.

Usage:  SysStateTracer.exe [switch] [arguments]
Without a switch the main window opens.

Switches:
  /?                                  Shows this help.
  /new-snap ["name"]                  Takes a snapshot with the scope saved in the New Snapshot window.
                                      Without a name: YYYYMMDD_HH-MM-SS_COMPUTERNAME, e.g. 20261027_14-30-00_OFFICE-PC.
  /delete-snap ["name"]               Deletes the snapshot; without a name, the newest one.
                                      Prints which snapshot was deleted.
  /rename-snap "old name" "new name"  Renames a snapshot. Both names are required.
  /compare-snap ["name1" "name2"] ["report.html"]
                                      Compares two snapshots and writes a self-contained HTML report.
                                      Without names: the newest two. Without a report path: results.html.
                                      A relative report path is placed in the program folder.
  /export ["name1" "name2"]           Exports what is new or modified in the newer snapshot:
                                      registry keys and values to Export_<name1>_vs_<name2>.reg, and files to the Files folder
                                      (keeping their folders), with Restore-Files.ps1 there to copy them back to their places.
                                      Without names: the newest two.

A snapshot is named by its file name, with or without .stsnap, in the snapshot folder; or give the path of
a .stsnap file anywhere. Put names and paths with spaces in quotes.
Comparisons and exports leave out everything matched by the Global Ignore list chosen in Preferences.

Folders:
  Snapshots:                       C:\Tools\SysStateTracer
  Reports, .reg files, Files, log: C:\Tools\SysStateTracer

Exit codes:
  0 success   1 wrong arguments   2 snapshot not found   4 file could not be read or written
  5 cancelled (Ctrl+C)

Run from a console opened with "Run as administrator" for complete snapshots; as a standard user,
folders and registry keys that need administrator rights are skipped.

Examples:
  Snapshot before and after installing an application:
    SysStateTracer.exe /new-snap "Before Office 2024 install"
    SysStateTracer.exe /new-snap "After Office 2024 install"
  Compare them and save the report in a reports folder:
    SysStateTracer.exe /compare-snap "Before Office 2024 install" "After Office 2024 install" "C:\Reports\Office 2024 install changes.html"
  Export the new registry values and files the installation added:
    SysStateTracer.exe /export "Before Office 2024 install" "After Office 2024 install"
    (gives Export_Before Office 2024 install_vs_After Office 2024 install.reg and Files\Program Files\...)
  Snapshot files in another folder, given by path:
    SysStateTracer.exe /new-snap "D:\Snapshots\Print server before patch.stsnap"
    SysStateTracer.exe /compare-snap "D:\Snapshots\Print server before patch.stsnap" "After Office 2024 install.stsnap"
  Compare the newest two snapshots into results.html:
    SysStateTracer.exe /compare-snap
  Keep a snapshot as a baseline under a clearer name, and delete one that is no longer needed:
    SysStateTracer.exe /rename-snap "After Office 2024 install" "Office 2024 baseline"
    SysStateTracer.exe /delete-snap "Before Office 2024 install"
  Nightly snapshot from a batch file, named with the date and time (waits for it and checks the exit code):
    start "" /wait SysStateTracer.exe /new-snap
    if errorlevel 1 echo Snapshot failed with code %ERRORLEVEL%
  From PowerShell, waiting for it and reading the exit code:
    $p = Start-Process SysStateTracer.exe '/new-snap "Before driver update"' -Wait -PassThru; $p.ExitCode

Everything printed is also written to SysStateTracer.log.
Documentation and source: https://github.com/IgorZaharov/SysStateTracer
```

The folders shown are the real ones on the PC where it runs.

## Details

- **Snapshot names** are file names, with or without `.stsnap`, in the snapshot folder. A path to a `.stsnap` file
  works everywhere too (F6): `/new-snap "D:\Snapshots\Before.stsnap"` saves the snapshot there, and
  `/rename-snap "Before" "D:\Archive\Before.stsnap"` moves it. Relative paths start from the current folder.
- **"Newest"** is the creation time stored in the snapshot, not the file date, which changes when a file is copied.
- **/new-snap** uses the scope saved in the New Snapshot window (by default all fixed drives, HKLM, HKU and services)
  and the snapshot cache, so a snapshot after the first is incremental. It prints a progress line every few seconds.
- **/compare-snap and /export** compare older → newer by creation time, whatever order the names are given in;
  the report shows the first name on the left. Both print the new / modified / deleted counts.
- **Global Ignore list:** comparisons and exports use the list chosen in Preferences (Remote, or Local; Remote
  falls back to Local when it cannot be read), and print which one was used. The rules also apply inside new
  folders and keys: for example, with `FS:C:\Program Files\App\logs` in the list, a new `App` folder is exported
  without its `logs` folder.
- **/export** writes the .reg file and copies files as described in [exports.md](exports.md), and writes
  `Files\Restore-Files.ps1`, which copies everything in `Files` back to its original place (F7). Locked files are
  skipped and listed; the export still succeeds. Ctrl+C stops the copy and keeps the files already copied (exit code 5).

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Wrong arguments: unknown switch, wrong number of names, invalid or already used name |
| 2 | Snapshot not found, or fewer than two snapshots for /compare-snap and /export |
| 3 | Not used since 1.0.0: a standard user can run every switch (F9) |
| 4 | A file could not be read or written (also a damaged snapshot) |
| 5 | Cancelled with Ctrl+C |
