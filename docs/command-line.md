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
  /new ["name"]                       Takes a snapshot with the scope saved in the New Snapshot window.
                                      Without a name: YYYYMMDD_HH-MM-SS_COMPUTERNAME, e.g. 20261027_14-30-00_OFFICE-PC.
  /del ["name"]                       Deletes the snapshot; without a name, the newest one.
                                      Prints which snapshot was deleted.
  /rename "old name" "new name"       Renames a snapshot. Both names are required.
  /report ["name1" "name2"] [filters] [/output:"report.html"]
                                      Compares two snapshots and writes a self-contained HTML report.
                                      Without names: the newest two. Without a report path: results.html.
                                      A relative report path is placed in the program folder.
  /export ["name1" "name2"] [filters] [/output:"folder" or "file.reg"]
                                      Exports what is new or modified in the newer snapshot:
                                      registry keys and values to Export_<name1>_vs_<name2>.reg, and files to the Files folder
                                      (keeping their folders), with Restore-Files.ps1 there to copy them back to their places.
                                      Without names: the newest two.
                                      /output: a folder (created if needed) gets the .reg file, Restore-Files.ps1 and the Files
                                      folder; a path ending in .reg names the .reg file, with the rest beside it.

Filters for /report and /export: only what they name is compared and exported.
  /registry:"key","key"               Registry keys and everything below them, e.g. "HKLM\SOFTWARE\App","HKCU\Software\App".
                                      HKCU means every user (HKU\*).
  /files:"folder","folder"            Folders or files and everything below them, e.g. "C:\Program Files\App".
  /services:"name","name"             Services by key name, e.g. "Spooler" (/report only).
  /environment:"var","var"            Environment variables as System\name or User\name, e.g. "System\Path" (/report only).
  Separate several with commas, or repeat the switch (for a path that contains a comma). * and ? work
  as in the Global Ignore list. With filters, an area without one is left out: /registry: alone exports
  only a .reg file. A filter that matches nothing gives a warning.

The switch names before 1.2.0 still work: /new-snap, /delete-snap, /rename-snap and /compare-snap.

A snapshot is named by its file name, with or without .stsnap, in the snapshot folder; or give the path of
a .stsnap file anywhere. Put names and paths with spaces in quotes.
Comparisons and exports leave out everything matched by the Global Ignore lists set in Preferences.

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
    SysStateTracer.exe /new "Before Office 2024 install"
    SysStateTracer.exe /new "After Office 2024 install"
  Compare them and save the report in a reports folder:
    SysStateTracer.exe /report "Before Office 2024 install" "After Office 2024 install" "C:\Reports\Office 2024 install changes.html"
  Export the new registry values and files the installation added:
    SysStateTracer.exe /export "Before Office 2024 install" "After Office 2024 install"
    (gives Export_Before Office 2024 install_vs_After Office 2024 install.reg and Files\Program Files\...)
  Export only the application's registry keys and folders into C:\Exports\Office (created if needed):
    SysStateTracer.exe /export "Before Office 2024 install" "After Office 2024 install" ^
      /registry:"HKLM\SOFTWARE\Microsoft\Office","HKCU\Software\Microsoft\Office" ^
      /files:"C:\Program Files\Microsoft Office","C:\ProgramData\Microsoft\Office" /output:"C:\Exports\Office"
    (gives C:\Exports\Office\Export_...reg, Restore-Files.ps1 and Files\Program Files\Microsoft Office\...)
  Only the registry, into a .reg file of its own name:
    SysStateTracer.exe /export "Before Office 2024 install" "After Office 2024 install" ^
      /registry:"HKLM\SOFTWARE\Microsoft\Office" /output:"C:\Exports\Office.reg"
  A report of the application's folders, keys and services only:
    SysStateTracer.exe /report "Before Office 2024 install" "After Office 2024 install" ^
      /files:"C:\Program Files\Microsoft Office" /registry:"HKLM\SOFTWARE\Microsoft\Office" ^
      /services:"ClickToRunSvc" /output:"C:\Reports\Office.html"
  Snapshot files in another folder, given by path:
    SysStateTracer.exe /new "D:\Snapshots\Print server before patch.stsnap"
    SysStateTracer.exe /report "D:\Snapshots\Print server before patch.stsnap" "After Office 2024 install.stsnap"
  Compare the newest two snapshots into results.html:
    SysStateTracer.exe /report
  Keep a snapshot as a baseline under a clearer name, and delete one that is no longer needed:
    SysStateTracer.exe /rename "After Office 2024 install" "Office 2024 baseline"
    SysStateTracer.exe /del "Before Office 2024 install"
  Nightly snapshot from a batch file, named with the date and time (waits for it and checks the exit code):
    start "" /wait SysStateTracer.exe /new
    if errorlevel 1 echo Snapshot failed with code %ERRORLEVEL%
  From PowerShell, waiting for it and reading the exit code:
    $p = Start-Process SysStateTracer.exe '/new "Before driver update"' -Wait -PassThru; $p.ExitCode

Everything printed is also written to SysStateTracer.log.
Documentation and source: https://github.com/IgorZaharov/SysStateTracer
```

The folders shown are the real ones on the PC where it runs.

## Details

- **Snapshot names** are file names, with or without `.stsnap`, in the snapshot folder. A path to a `.stsnap` file
  works everywhere too (F6): `/new "D:\Snapshots\Before.stsnap"` saves the snapshot there, and
  `/rename "Before" "D:\Archive\Before.stsnap"` moves it. Relative paths start from the current folder.
- **"Newest"** is the creation time stored in the snapshot, not the file date, which changes when a file is copied.
- **/new** uses the scope saved in the New Snapshot window (by default all fixed drives, HKLM, HKU and services)
  and the snapshot cache, so a snapshot after the first is incremental. It prints a progress line every few seconds.
- **/report and /export** compare older → newer by creation time, whatever order the names are given in;
  the report shows the first name on the left. Both print the new / modified / deleted counts. In the report, a new
  or deleted folder or key opens like in the comparison window, to show what it holds (up to 250,000 such rows).
- **Global Ignore list:** comparisons and exports use the local list together with the remote list, or the local
  list only, as set in Preferences (the local list alone when the remote one cannot be read), and print which. The rules also apply inside new
  folders and keys: for example, with `FS:C:\Program Files\App\logs` in the list, a new `App` folder is exported
  without its `logs` folder.
- **/export** writes the .reg file and copies files as described in [exports.md](exports.md), and writes
  `Files\Restore-Files.ps1`, which copies everything in `Files` back to its original place (F7). Locked files are
  skipped and listed; the export still succeeds. Ctrl+C stops the copy and keeps the files already copied (exit code 5).
- **Switch names** (1.2.0, issue #8): `/new`, `/del`, `/rename`, `/report`, `/export`. The earlier names
  `/new-snap`, `/delete-snap`, `/rename-snap` and `/compare-snap` still work, so existing scripts need no change.

## Filters and /output: (issue #8)

`/report` and `/export` take filters, so only what they name is compared and exported:

| Switch | Takes | Example |
|---|---|---|
| `/registry:` | Registry keys, each with everything below it | `/registry:"HKLM\SOFTWARE\App","HKCU\Software\App"` |
| `/files:` | Folders or files, each with everything below it | `/files:"C:\Program Files\App","C:\ProgramData\App"` |
| `/services:` | Services by key name (`/report` only; services are not exported) | `/services:"Spooler","BITS"` |
| `/environment:` | Environment variables as `System\name` or `User\name`; `User\*` is every user variable (`/report` only) | `/environment:"System\Path","User\TEMP"` |
| `/output:` | `/report`: the `.html` report. `/export`: a folder, or a `.reg` file | `/output:"C:\Exports\App"` |

- **Several values:** separate them with commas, or repeat the switch. A path that contains a comma must be given
  on its own switch: `/files:"C:\Data\A, B" /files:"D:\Tools"`. `--output:` and `-output:` work too.
- **Paths** are written like Global Ignore rules without the `FS:` / `REG:` / `SVC:` prefix: `*` and `?` work
  (`/files:"C:\Users\*\AppData\Roaming\App"`), **HKCU means every user** (`HKU\*`, the snapshot holds every user),
  HKCR is `HKLM\SOFTWARE\Classes`, and `CurrentControlSet` matches the stored `ControlSet00x` keys. Each run prints
  the filters, with the path each registry filter is matched as.
- **With filters, an area without a filter is left out:** `/registry:` alone gives a report with only those keys,
  and an export with only a .reg file and no copied files (`Files: not copied (no /files: filter)`). The Global
  Ignore list still applies inside what the filters keep.
- **What each filter matched** is printed after the comparison, with the new / modified / deleted counts that are
  left. A filter that matches nothing prints a warning (check the path); the run still succeeds. A filter that
  points inside a folder or key that is new or deleted as a whole keeps that whole folder or key, with a note.
- **`/export /output:`** — a folder is created if it does not exist, with parent folders. It gets the .reg file
  (`Export_<name1>_vs_<name2>.reg`), `Restore-Files.ps1` and the copies in a `Files` folder:

  ```text
  SysStateTracer.exe /export "1" "2" /registry:"HKLM","HKU" /files:"C:\1","C:\2" /output:"C:\test"

  C:\test\Export_1_vs_2.reg
  C:\test\Restore-Files.ps1          copies everything in C:\test\Files back to its place
  C:\test\Files\1\...
  C:\test\Files\2\...
  ```

  A path ending in `.reg` (`/output:"C:\test\App.reg"`) names the .reg file; `Restore-Files.ps1` and `Files` go
  beside it. Without `/output:`, as before: the program folder, with `Restore-Files.ps1` inside `.\Files`.
- **`/report /output:`** must end in `.html`; a relative path is placed in the program folder. The report path can
  still be given as the last argument without `/output:`.
- **Errors** (exit code 1, nothing is compared): an unknown option such as `/file:`, an empty value or list entry,
  a filter that is not valid (for example a registry key that does not start with HKLM, HKU, HKCU, HKCR or HKCC),
  `/output:` twice, an `.html` path for `/export`, a non-`.html` path for `/report`, `/services:` or `/environment:` with `/export`, or
  a filter with `/new`, `/del` or `/rename`. The output folder is checked before the snapshots are read: when it
  cannot be created or written to, or is a file, the reason is printed and the exit code is 4.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Wrong arguments: unknown switch or option, wrong number of names, invalid or already used name, a filter or `/output:` that is not valid |
| 2 | Snapshot not found, or fewer than two snapshots for /report and /export |
| 3 | Not used since 1.0.0: a standard user can run every switch (F9) |
| 4 | A file could not be read or written (also a damaged snapshot), or the output folder cannot be created or written to |
| 5 | Cancelled with Ctrl+C |
