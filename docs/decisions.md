# Decisions

Confirmed by the project owner on 27 Oct 2026 (plan Step 1).

| # | Topic | Decision |
| --- | --- | --- |
| D1 | Runtime | C# 14, .NET 10 LTS, Windows Forms, self-contained single-file `SysStateTracer.exe` (win-x64). Performance is favoured over official support on regular Windows 10 22H2, which is not on Microsoft's .NET 10 support list; it is tested explicitly on a clean VM. |
| D2 | Comparison view | Only new, modified and deleted items plus the parents needed to reach them. No "show unchanged" option. |
| D3 | Copy folder to `.\Files` | Copies the new and modified files inside the folder and its subfolders, with a progress window and Cancel. |
| D4 | `.\Files` path | System drive: `.\Files\ProgramData\Test`. Other drives: `.\Files\_D\Data\X`. |
| D5 | Non-important marks | Kept only while the comparison window is open; not saved. |
| D6 | Repository | https://github.com/IgorZaharov/SysStateTracer |
| D7 | Elevation | ~~Manifest requests administrator rights at every launch.~~ Replaced by F9 (28 Oct 2026). |
| D8 | Code signing | Releases are unsigned; each release publishes the exe's SHA-256. |
| D9 | Default scope | All fixed drives, HKLM without SAM/SECURITY, HKU, Win32 services; kernel drivers off. |
| D10 | Cold file-system scan | Whole NTFS drives are read from the master file table (one sequential read of the table instead of one or more reads per folder). Folder listing remains for other file systems, single-folder roots, and whenever the table cannot be read. Confirmed 27 Oct 2026; see [benchmarks.md](benchmarks.md). |
| D11 | Name | The project is named **SysStateTracer** (renamed from SysSnapUtil on 27 Oct 2026): `SysStateTracer.exe`, repository https://github.com/IgorZaharov/SysStateTracer, snapshot files `.stsnap` with the signature "STSNAP". The repository starts from one fresh commit. |

## Other confirmed behaviour

- Revert Value asks "Do you want to revert to value from Snapshot <name>?", writes a `.reg` backup to `Backups\`, then restores the older snapshot's value.
- Known noisy locations (Temp, Prefetch, logs, ETW traces, UserAssist, BAM) are default entries in the Global Ignore list and can be removed.
- Target test systems: Windows 10 22H2 x64 and Windows 11 25H2 x64.

## Build mode

GitHub + CI (plan §4.3 mode B): code is pushed to this repository and GitHub Actions on `windows-latest` builds, tests and publishes the exe.


## Feature requests (docs/featurerequest.md, 27 Oct 2026)

| # | Request | How it is built | Phase |
| --- | --- | --- | --- |
| F1 | Global Ignore list location: Remote (URL or network share) or Local, chosen with radio buttons in Preferences; Remote falls back to Local when not reachable; default Remote = the list in this repository | Local = `SysStateTracer.ignore.txt` beside the exe (created from the built-in defaults when missing). Remote = `http(s)://` URL or `\\server\share\file.txt`, read with a timeout. Default Remote: `https://raw.githubusercontent.com/IgorZaharov/SysStateTracer/main/ignore/SysStateTracer.ignore.txt`. Replaces the "local rules + merged URL list" design of Step 15. Remote is selected by default. **Add to Global Ignore list** always writes to the Local file; while Remote is in effect the new rule applies once Local is selected or the remote list is unreachable (option A, confirmed 27 Oct 2026). | C (rework, done) + I (Preferences, done) |
| F2 | Application icon: small HDD on black, green and blue horizontal lines overlapping on the right like file names; small and large Explorer views | `src/SysStateTracer/SysStateTracer.ico`, 16–256 px, each size drawn separately by `tools/icon/make_icon.py`; used for the exe and every window | done |
| F3 | Export the whole tree from a top-level folder; items marked non-important are left out | Export is offered on every node of the comparison tree including the top-level ones (drives, `HKLM`, `HKU`); it covers the node's whole changed subtree and skips anything marked non-important. Engine done in `DiffExport` (.reg and file copy); the menu item comes with the comparison window | G (engine, done) + I (menu, done) |
| F5 | Pictures for the themes in Preferences → General | A small drawing of the main window in each theme above Follow Windows (half light, half dark), Light and Dark; clicking a picture chooses it; the chosen one has an accent border | I, done |
| F6 | Command-line snapshot names may carry .stsnap or be paths | Every switch accepts `name`, `name.stsnap` or a path to a `.stsnap` file; `/new-snap` with a path saves there, `/rename-snap` with a path moves the file | H, done |
| F7 | `/export` writes a PowerShell script into .\Files that copies everything back | `Files\Restore-Files.ps1` maps the folder layout back (system drive, `_D` → `D:`), supports `-WhatIf`; also written by Copy to .\Files in the comparison window | G/H, done |
| F8 | Manual.pdf with screenshots | Built by `tools/manual/make_manual.py` in CI from the screenshots and `/?` text of the same build; title page, contents, Introduction, Common usage, Command-line usage, Editing the Global Ignore list, Preferences; on the `ui-preview` pre-release and attached to every release | J, done |
| F9 | Run as a standard user; no UAC prompt at start | Manifest `asInvoker`: the program runs with the rights of the user who starts it. Without administrator rights a bar in the main window explains what is missing and offers **Restart as administrator** (UAC prompt then); New Snapshot greys out folders and keys the user cannot read; snapshots record whether they were taken as administrator, the cache is not reused across different rights or users, and a comparison of a standard-user and an administrator snapshot says why protected items show as new or deleted. The command line runs every switch as a standard user with a note (exit code 3 is no longer used). | I/J, done |
| F10 | Values inside a new or deleted key | A whole new or deleted key stays one comparison item (counts, exports, revert and ignore rules work on it), and expanding it lists its subkeys and values straight from the snapshot that has them. Those rows are for reading and Jump to only. | J, done |
| F11 | Files inside a new or deleted folder | The same for folders: expanding a whole new or deleted folder lists its folders, then its files, with dates and sizes. Global Ignore rules still apply to what is listed. | J, done |
| F12 | Windows remember size and position | Saved per window in SysStateTracer.config when it closes (`windows`); a position is used only if the window would still be on a screen, otherwise only the size. Dialogs keep opening centred on their owner. | J, done |
| F13 | One theme choice at a time | Each theme button sits in its own column under its picture, so Windows does not group them; choosing one clears the other two. | J, done |
| F14 | Window sizes | Main window 850 × 450 at first; every other window at most 580 px tall (window size, at 100 % scaling). | J, done |
| F15 | Blank columns when the mouse is between rows | The list sometimes repaints a row without its other columns; every column is now drawn with the row. | J, done |
| F16 | Sort by column headers | The "Sort by" drop-down is gone; a header click sorts, a second click reverses, an arrow marks the column. Saved as `sortOrder` + `sortDescending`; default Created, newest first. | J, done |
| F17 | Direct download links | README starts with download buttons for the exe and Manual.pdf using `releases/latest/download/…`, which always points at the newest full release (release candidates are pre-releases and are not "latest"). | J, done |
| F18 | Comparison keeps its view after Add to Global Ignore list | Comparing again builds new rows, so the open folders and keys, the selection, the first row on screen and the marks are kept by path and restored; rows that are now ignored are simply gone. Refresh does the same. | J, done |
| F19 | No hover tool tips in the comparison | They covered the next rows. The status bar shows the full path (and item count) of the one selected row instead. | J, done |
| F20 | Rules can be changed before they are added | Add to Global Ignore list shows the rules in an editable box, one per line, checked as they are typed (e.g. a user name replaced by `*`). | J, done |
| F21 | Icons for drives, folders, files, keys and values | One glyph before each name, drawn with the row: only the rows on screen are painted, so the cost is one more text draw per visible row — the frame-time test (a million rows) still passes well under a frame. | J, done |
| F22 | Local and remote list together | The local list always applies; "Also use the shared remote list" (default on) adds the remote rules. Before, choosing Remote silently ignored local rules. Settings keep the old names (`remote` = both, `local` = local only). | J, done |
| F23 | Keyboard in message boxes | Every dialog takes the focus when it opens and its main button has it (always outlined): Enter presses it, Left/Right move between the buttons. | J, done |
| F24 | Keyboard in New Snapshot | Start is the default button and has the focus: Enter starts the snapshot; Left/Right move between Start, Stop and Close. | J, done |
| F25 | Edit rules in Preferences | Add rule… (typed, with wildcards) and Edit… (also double-click, F2, Enter) on the local list, with the same checked rules box. | J, done |
| F26 | Wildcard rules inside new folders | A new or deleted folder or key is one comparison item; its item count, the category totals and the expanded contents now leave out what the rules match inside it (e.g. `FS:C:\temp\*`, `FS:C:\Users\*\AppData\Local\Temp`). Exports already did. | J, done |
| F27 | Change colours in every column | Green, yellow and red apply to the snapshot columns (dates, sizes, values) too. | J, done |
| F28 | Unblock instructions | README and manual explain Properties → Unblock, with a screenshot of the real Windows Properties window taken in CI. | J, done |
