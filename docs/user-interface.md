# Windows

All windows follow the Light, Dark or Windows theme chosen in Preferences, and switch at once when it
changes. Screenshots of every window in both themes are saved by the UI tests on each push and kept on the
[`ui-preview`](https://github.com/IgorZaharov/SysStateTracer/releases/tag/ui-preview) pre-release.

Every window opens where and as large as it was when it was last closed, maximized too (F12); a place on a monitor
that is no longer connected is not used. The first time, the main window is 850 × 450 and no other window is
taller than 580 pixels (F14, at 100 % display scaling).

## Main window (W1)

- **Standard user (F9):** the program starts without a UAC prompt, with the rights of the user who starts it.
  Without administrator rights a yellow bar says what is missing and offers **Restart as administrator**.

- **Sidebar:** + New Snapshot, Delete Snapshot, Preferences, Help/About; Exit at the bottom. Hover shows the name.
- **List:** Name, Created, Computer, Duration, Size, read from the snapshot headers only (100 snapshots list in
  well under half a second). Click a column header to sort by it, again to reverse; an arrow shows which (F16).
  Dates, durations and sizes start with the newest, longest or largest. The default is newest first, and the choice
  is remembered. The list follows the
  folder: snapshots added, deleted or renamed outside the program appear at once.
- **Right-click:** Compare with ▸, Rename, Delete, Properties.
- **Compare:** drag one snapshot onto another (the target row is highlighted), or use Compare with. The older
  snapshot is always shown on the left.
- **Keys:** Ctrl+N new snapshot · Del delete · F2 rename · Enter properties · F5 refresh · Ctrl+A select all · F1 help.

## Snapshot Properties (W2)

General (name, file, size, created in local time and UTC, computer, user, Windows, version), Timing (total and per
part, Full or Incremental and why), Scope, Global Ignore rule count, Counts, and the skipped items with their
reasons.

## New Snapshot (W3)

- **Name:** prefilled with `YYYYMMDD_HH-MM-SS_COMPUTERNAME`; problems (invalid characters, name in use) show at once.
- **What to include:** drives, registry (HKLM, HKU with each user) and services, with tri-state checkboxes.
  Sub-folders and sub-keys load when expanded. Items in the Global Ignore list are unchecked and marked
  "(ignored)"; to include one, remove its rule in Preferences. Without administrator rights, folders and keys
  your user cannot read are greyed out as "(no access)", with a link to restart as administrator. Unchecking a folder or key inside an included
  drive or hive leaves it out of this and later snapshots. The scope is remembered and also used by `/new-snap`.
- **Full rescan:** reads everything again instead of reusing unchanged parts of the previous snapshot.
- **Start / Stop:** progress bar, elapsed time ticking every second, current phase and path, items per second.
  Stop saves nothing; closing the window while it runs asks first.

## Comparison (W4)

- **Top bar:** snapshot 1 · ⇄ Swap · snapshot 2 · Refresh. Swap is instant: columns trade places and new ↔ deleted.
- **Tree:** File system, Registry and Services, each with its counts; only changed items and the folders or keys
  leading to them. New is green (+), modified yellow (~), deleted red (−). A parent shows how many changes it
  holds; a new or deleted folder shows how many items it held. Hover for full paths, exact sizes and full values.
- **Inside a new or deleted folder or key (F10, F11):** expanding it lists what it holds — folders and files with
  dates and sizes, or subkeys and values with their data — from the snapshot that has them (Global Ignore rules
  still apply). They are shown for information: Jump to opens Explorer or Regedit there; the other actions work on
  the folder's or key's own row.
- **Selection:** Ctrl+click, Shift+click, arrow keys, Ctrl+A. Right-click or the menu key for actions.

| Action | Available for | What it does |
|---|---|---|
| Jump to | one item | Explorer with the file selected, Regedit at the key, or the Services console with the service selected |
| Mark as non-important | any items | Strikes them through until the window closes; left out of exports |
| Add to Global Ignore list | any items | Adds rules to the local list and hides the items |
| Export to .REG | registry items, also a whole hive or the Registry row | New and modified keys and values from the newer snapshot |
| Copy to .\Files | folders and files, also a drive or the File system row | New and modified files, with a progress window and Cancel; skipped files are listed |
| Revert Value | registry keys and values | Asks "Do you want to revert to value from Snapshot X?", lists current → restored values, writes a backup to `Backups\`, then reverts |

The tree list paints only the rows on screen: with a million rows, scrolling and expanding stay within a frame.

## Preferences (W5)

- **General:** snapshot folder (Browse…, or the program folder; it must be writable) and theme, each choice with a
  small picture of the window in that theme (F5; click the picture or the button). The theme applies live; Cancel
  restores it.
- **Global Ignore list:** which list is used — Remote (URL or network file, with Check now) or Local
  (`SysStateTracer.ignore.txt` beside the program). The local list's rules can be added (folder, registry key,
  service), removed, imported and exported. Comments in the file are kept.

## Help / About (W6)

Version, the GitHub link, and the command-line help (the same text as `SysStateTracer.exe /?`).

## Manual test script

Run on a Windows PC with the published `SysStateTracer.exe`. It starts without a UAC prompt; click
**Restart as administrator** in the main window for complete snapshots (and once without, to see the standard-user view):

1. Start the program with no snapshots: the list says there are none. Click **+**, keep the default name, Start.
   The timer ticks, the phase and path change, Stop becomes available; at the end the summary shows and the new
   snapshot is selected in the list.
2. Install or change something small (e.g. a program), take a second snapshot. It should say "incremental".
3. Drag the first snapshot onto the second: the comparison opens with the older one on the left.
4. Expand the categories; check colours and glyphs; hover a file and a registry value for tooltips. Press ⇄.
5. Right-click a changed registry value → Revert Value → check the list → Revert. Check the value in Regedit
   and that `Backups\Revert_…reg` exists; import it to undo.
6. Right-click a new folder → Copy to .\Files; Cancel once, then let it finish; open the folder.
7. Right-click the Registry row → Export to .REG; open the file in a text editor.
8. Right-click a service → Jump to: the Services console opens with it selected.
9. Mark an item as non-important; close and reopen the comparison: the mark is gone.
10. Preferences: switch Dark/Light (all windows change at once), Cancel (it goes back); change the snapshot folder.
11. In the list: F2 rename, Del delete (several at once), Enter properties (with skipped items), F5.
