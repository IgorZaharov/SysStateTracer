# Changelog

## [1.0.0]

The first release.

### Snapshots
- Runs without a UAC prompt and for standard users; **Restart as administrator** for complete snapshots. Items a standard user cannot read are greyed out in New Snapshot, and snapshots record which rights they were taken with.
- Snapshots of folders and files (names, sizes, dates and attributes; file contents are never read), the registry (HKLM and every user under HKU) and Windows services, in one compressed `.stsnap` file.
- Whole NTFS drives are read straight from the master file table: a typical system drive in seconds.
- Folders behind junctions and symbolic links that lead outside the scanned folders (e.g. a Desktop redirected to a network share) are read through the link and recorded under the local path.
- Consecutive snapshots are faster: the NTFS change journal tells SysStateTracer what changed, and unchanged folders and registry keys are reused from the previous snapshot. A drive without a journal is read in full.
- Default names `YYYYMMDD_HH-MM-SS_COMPUTERNAME`; anything that cannot be read is skipped and listed in the snapshot's properties.
- `HKLM\SAM` and `HKLM\SECURITY` are left out by default.

### Comparison
- Expanding a new or deleted folder or registry key lists the files, folders, subkeys and values it holds.
- Icons for drives, folders, files, keys and values; change colours in every column; the selected item's path in the status bar instead of hover tips.
- Adding Global Ignore rules (they can be edited first) or Refresh keeps the tree open as it was.
- The HTML report (/compare-snap) also lists what new and deleted folders and keys hold, to any depth.
- Show unchanged: opened folders and keys also list the items that did not change.
- Only what changed is shown, with the folders and keys leading to it: new in green (+), modified in yellow (~), deleted in red (−). Swap the two snapshots instantly.
- A million changed items scroll and expand without delay.
- Right-click actions: jump to Explorer, Regedit or the Services console; mark as non-important; add to the Global Ignore list; export registry changes to a `.reg` file; copy new and modified files to `.\Files` keeping their folders; revert registry values to the older snapshot (with a backup first). Exports also work on a whole drive, hive or category.

### Global Ignore list
- The local list always applies, together with the shared remote list unless that is turned off. Rules can be typed and edited in Preferences, with wildcards, which also apply inside new folders.
- Remote (a URL or network file shared by several PCs, with the local list as fallback) or Local (`SysStateTracer.ignore.txt` beside the program), chosen in Preferences.
- Comes with rules for places that change all the time (temporary files, caches, logs, telemetry keys); every rule can be removed.

### Command line
- `/?`, `/new-snap`, `/delete-snap`, `/rename-snap`, `/compare-snap` (self-contained HTML report), `/export`. Exit codes for scripts. See [docs/command-line.md](docs/command-line.md).
- Snapshots can be given by name, with or without `.stsnap`, or by path.
- Copied files come with `Restore-Files.ps1`, which puts them back where they came from.

### Windows
- Main window, New Snapshot, Comparison, Snapshot Properties, Preferences and Help/About, in Light, Dark or the Windows theme.
- Every window comes back at the size and place it was closed. The snapshot list sorts by clicking a column header.

### Documentation
- `Manual.pdf`, the user manual with screenshots, comes with every release; the README links straight to the latest exe and manual.
