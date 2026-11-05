# Changelog

## [Unreleased]

### Comparison window
- **Del** marks the selected items as non-important, in the tree and in the list; Del again unmarks them. The menu shows the key beside **Mark as non-important**.

## [1.2.3] — 2026-11-05

### Comparison window
- **Mark as non-important** marks the folders and keys above the item too when nothing else in them changed: marking the only change in a branch strikes through the whole branch, up to the first folder or key that still holds another change. Unmarking an item unmarks the branch above it.

### Windows
- **Preferences:** buttons are as wide as their labels ("Add registry key…", "Check now", "Program folder" and others were cut off), and the window opens wide enough to show them on one row.
- **Preferences:** the snapshot folder and remote list boxes are centred on the buttons beside them, and each theme's choice is centred under its picture.

## [1.2.2] — 2026-11-05

### Download
- The download is **`SysStateTracer.exe`** itself again, not a zip: the links in the README, the manual and the release lead straight to it (with `SysStateTracer.exe.sha256` and `Manual.pdf`). Before the first start, right-click the exe → **Properties**, tick **Unblock**, click **OK**.
- `SysStateTracer.exe` is no longer stored compressed inside itself (from 1.2.1-rc.1), so it does not look like a packed program to antivirus software: browsers could block the download of 1.2.0 with **"Virus detected"**, a false positive. The exe is 118 MB instead of 52 MB, and starts slightly faster.

### New home on GitHub
- The project moved to **https://github.com/igorzakharovdev-design/SysStateTracer** (the old repository no longer exists). Help / About, `/?`, the manual and the README link there.
- **Global Ignore list:** the default Remote list is now read from the new repository. Settings that still hold the old default are moved to the new one when SysStateTracer starts; a location you typed yourself is kept. Versions up to 1.2.x can no longer reach the old list and use the Local one; update, or paste the new address into Preferences there (their **Default** button still gives the old one).

### Snapshots
- **Environment variables** (request 40): each snapshot now records the System variables and your user variables, as the Environment Variables window shows them (values as written, so `%SystemRoot%` is not expanded). New Snapshot has an **Environment variables** item, ticked by default; Properties counts them.
- In the comparison, **Environment variables** is a fourth category beside Services: select it to list the variables, with System or User, the type and the value; a changed value is highlighted, and the details show both values. Jump to opens Regedit at the key that holds the variable. They are compared only when both snapshots have them (snapshots taken with an earlier version have none).
- Global Ignore list: **`ENV:`** rules, e.g. `ENV:User\OneDrive`, or `ENV:User` for every user variable. Command line: **`/environment:"System\Path"`** filters a `/report`. The HTML report lists the variables too.

### Comparison window
- **Expand all / Collapse all** in the tree's right-click menu (request 38): opens or closes the folder or key and everything below it.
- **HKCU beside HKLM and HKU** (request 41): your own registry key is a hive of its own at the top of the tree, as in Regedit, instead of "HKCU (S-1-5-21-…)" below HKU. HKU keeps the other users, and its count no longer includes your changes; with Only changes it is left out when nothing else in it changed. The path above the list says HKCU too.
- **Long values in the details** (request 42), e.g. a Path variable: up to three lines, broken between folders; a longer one shows three lines at a time with a scroll bar beside it (mouse wheel, click or drag). Values are shown whole up to 32,768 characters (they were cut at 600). The details are a little taller to make room.
- **Tab** goes from the tree to the list, and **Shift+Tab** back (request 43); it used to stop on the divider between them.
- **Keys in the tree** (request 39), on the selected row: **\*** expands all, **/** collapses all, **+** opens one level more, **−** closes the deepest open level. Expand all stops at 250,000 rows in the tree and says so in the status bar.

### Windows
- **Every window comes back where it was closed** (request 37): Preferences, Help / About, Snapshot Properties and Copy to Files now remember their place as well as their size, like the main window, Comparison and New Snapshot. Message boxes still open centred on their window.
- **Preferences** opens tall enough to show the whole General page (the Lists setting was cut off at the bottom), also when it was closed smaller, but never taller than the screen; on a small screen the page scrolls (request 44).

### Windows 11 look
- **Comparison starts in the tree:** the keyboard focus is in the left pane when the window opens, so Tab goes from there to the list.
- **Folders first:** Help / About and `/?` list the snapshot and data folders right after the version, before the switches.
- **Narrow main window:** the command buttons from the right end show only their icon instead of being cut off (Properties was clipped).
- **Sharper icons:** folder and key icons in the comparison tree and its legend are drawn as smoothed shapes; drawn as text, their upright sides could disappear (at 125 % display scaling, for example).
- **Mica behind the whole window**, as in the Settings app (request 45): the window body shows your wallpaper, blurred and tinted dark or light, and cards are a translucent lighter layer over it; lists, trees and text boxes take matching solid colours. SysStateTracer paints it itself (Windows puts Mica only behind the title bar of a window like this), so it follows the wallpaper, turns plain while the window is inactive, and is off where Windows turns Mica off: before Windows 11 22H2, with transparency effects off, in high contrast and over Remote Desktop.
- **Mica** in the title bar on Windows 11 22H2 and later (the first Windows 11 release keeps a plain title bar); the window body in Mica's colours, with lists and trees on a lighter card. Text in Segoe UI Variable at the Windows 11 sizes (Segoe UI on Windows 10).
- Buttons, list rows and selection as in Windows 11: rounded, a soft blue selection with a blue bar at the row's left end, a light fill under the mouse.
- **Main window:** a command bar replaces the sidebar: New snapshot, **Compare** (new: the two selected snapshots, or the selected one with one picked from a menu), Rename, Delete, Properties; Preferences and Help at the right end. Exit is gone: close the window. The administrator warning is a Windows 11 warning bar with a close button.
- **Comparison window:** **Show all / Only changes** tabs; the tree opens with arrows instead of dotted lines and + / − boxes, in larger text, with the changes of File system, Registry and Services as **+20 ~1 −1** in their colours; the selected folder as a path bar (C:\ › Program Files › Contoso); OLDER / NEWER as small tags.
- **New Snapshot, Snapshot Properties, Preferences:** the window body in Mica's colours with the content on cards: the scope tree, each group of Properties, each Preferences setting and the Global Ignore list. Preferences' **General / Global Ignore list** and **Local list / Remote list** are tabs as in the comparison window. Without administrator rights New Snapshot shows the same warning bar as the main window, with **Restart as administrator** (it was a link). New Snapshot now also comes back where it was closed, not only at its size.
- **Every dialog:** the buttons sit in a footer below a thin line, as in Windows 11 dialogs. The Properties scroll bar is dark in the dark theme.

## [1.2.0] — 2026-11-03

### Comparison window
- **OLDER / NEWER** before each snapshot name, in the top bar and above the two columns of the details, by when the snapshots were taken. The captions move with the snapshots on Swap (issue #6).

### Command line
- **Shorter switch names:** `/new`, `/del`, `/rename`, `/report` (was `/compare-snap`) and `/export`. The earlier names still work.
- **Filters** for `/report` and `/export` (issue #8): `/registry:"HKLM\SOFTWARE\App","HKCU\Software\App"`, `/files:"C:\Program Files\App","C:\ProgramData\App"` and, for reports, `/services:"Spooler"`. Several paths separated by commas, or the switch repeated; wildcards as in the Global Ignore list. Only what the filters name is compared and exported; each filter's matches are printed, with a warning when one matches nothing.
- **`/output:`** for both: the report's `.html` path, or for `/export` a folder (created if needed) that gets the `.reg` file, `Restore-Files.ps1` and the copied files in `Files`; a `.reg` path names the .reg file, with the rest beside it. When the folder cannot be created or written to, the reason is printed before anything is read (exit code 4).

### Preferences
- Global Ignore list: **Local list** and **Remote list** tabs below the settings (issue #7). The Remote list tab shows the shared remote list's rules, read-only; it is read when the tab is first opened and again with **Check now**. Each tab keeps its scroll position. The buttons still add to the local list.

### Fixed
- **Memory** is given back to Windows a moment after a comparison window or New Snapshot closes (issue #9). The garbage collector kept what the closed window had used: with snapshots of a million files and 200,000 registry values, the working set stayed at about 400 MB after the window closed; now it drops to about 110 MB.
- Copy to .\Files could update its progress from a background thread (the same fix New Snapshot got in 1.1.0).
- Lists could fail to repaint while their rows were being replaced.
- `Restore-Files.ps1` no longer tries to copy a restore script from an earlier export to the system drive.

## [1.1.0] — 2026-10-29

### Comparison window
- **Two panes**, like Explorer: folders and registry keys on the left, joined by dotted lines with + / − boxes; the files of the selected folder, the values of the selected key, or all services on the right. Click a column heading to sort; by name at first.
- **Show all / Show only changes** above the tree (replaces Show unchanged), remembered between sessions.
- **Include subfolders**: the list also shows the files and values of every folder or key below the selected one.
- **Details** below the list: the selected item in both snapshots side by side, with the differences highlighted; changed values are highlighted in the list too.
- Colours: new folders and keys are green, modified **orange** (also in the dark theme, which used yellow), deleted red, and a folder or key that holds changes is **blue**; unchanged ones are in normal text. A **legend** in the status bar explains them.

### Fixed
- User profiles on FSLogix Profile Containers were missing from snapshots: FSLogix keeps the profile on a separate virtual disk and its driver shows it at `C:\Users\<name>`, while drive C: itself holds only an empty folder there, which is what a whole-drive scan read (and C:'s change journal never sees changes in the profile). Where FSLogix is installed, SysStateTracer now compares each profile folder with drive C:'s file table; a profile whose contents come from elsewhere is read through Windows, in full, on every snapshot, and recorded under `C:\Users\<name>`.
- Folders with a volume mounted on them (a VHD or a second disk mounted in a folder) were skipped; the mounted volume is now read and recorded under the folder.
- New Snapshot could crash while showing progress or finishing (a window update from a background thread).
- Snapshot Properties cut long lines off (how each folder was read, why a snapshot was incremental); they now wrap.

### Download
- The program now comes as **SysStateTracer.zip** with the exe inside: browsers warn less about it, and only the zip needs **Unblock** (Properties) before **Extract All**. README and manual links point to the zip.

### Preferences
- **Show grid lines** and **Use zebra striping** (Preferences → General → Lists) for the snapshot list and the comparison window. Both are off by default and apply at once.

## [1.0.0] — 2026-10-28

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
