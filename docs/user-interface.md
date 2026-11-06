# Windows

All windows follow the Light, Dark or Windows theme chosen in Preferences, and switch at once when it
changes. Screenshots of every window in both themes are saved by the UI tests on each push and kept on the
[`ui-preview`](https://github.com/IgorZaharov/SysStateTracer/releases/tag/ui-preview) pre-release.

Every window opens where and as large as it was when it was last closed, maximized too (F12): the main window,
Comparison, New Snapshot, Snapshot Properties, Preferences, Help / About and Copy to Files. A place on a monitor
that is no longer connected is not used. Message boxes and the small input boxes open centred on their window. The
first time, the main window is 850 × 450 and no other window is taller than 580 pixels (F14, at 100 % display scaling), except Preferences, which opens tall
enough for its whole General page (request 44; never taller than the screen, and the page scrolls on a small one).

## Main window (W1)

- **Standard user (F9):** the program starts without a UAC prompt, with the rights of the user who starts it.
  Without administrator rights a warning bar says what is missing and offers **Restart as administrator**; its close
  button hides it until the next start.

- **Look (Windows 11):** Mica in the title bar on Windows 11 22H2 (build 22621) and later, and behind the whole window as
  in Settings (request 45): the wallpaper, blurred and tinted, painted by SysStateTracer, with cards as a translucent
  lighter layer and lists, trees and text boxes in matching solid colours. It turns plain while the window is inactive
  and is off with transparency effects off (Settings → Personalization → Colors), in high contrast and over Remote
  Desktop. The list on a lighter card; rounded selection with a blue bar at the row's left end. The other windows share these colours, and
  their buttons sit in a footer below a thin line, as in Windows 11 dialogs.
- **Command bar:** **New snapshot**, **Compare** (the two selected snapshots, or the selected one with one picked from
  a menu), Rename, Delete, Properties; Preferences and Help at the right end. In a window too narrow for every label,
  the buttons from the right end (Properties first) show only their icon; the tooltip names them. Close the window to exit.
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
reasons. Each group is a card with its heading above it, as in Windows 11 Settings.

## New Snapshot (W3)

- **Name:** prefilled with `YYYYMMDD_HH-MM-SS_COMPUTERNAME`; problems (invalid characters, name in use) show at once.
- **What to include:** drives, registry (HKLM, HKU with each user), services and environment variables, with
  tri-state checkboxes. **Environment variables** are the System variables and the user variables of the user who
  takes the snapshot, as the Environment Variables window shows them (values not expanded).
  **Permissions** (off by default) also records each file's, folder's and key's own permissions: owner,
  inheritance on or off, and the entries set on the item itself (inherited ones are left out). It costs little
  with administrator rights on NTFS drives and for the registry, and one more read per file when folders are listed.
  Ticking it shows a warning: "Enabling the permission scan significantly increases scanning time".
  Sub-folders and sub-keys load when expanded. Items in the Global Ignore list are unchecked and marked
  "(ignored)"; to include one, remove its rule in Preferences. The tree is on a card. Without administrator rights,
  folders and keys your user cannot read are greyed out as "(no access)", and a warning bar at the top offers
  **Restart as administrator**. Unchecking a folder or key inside an included
  drive or hive leaves it out of this and later snapshots. The scope is remembered and also used by `/new`.
- **Full rescan:** reads everything again instead of reusing unchanged parts of the previous snapshot.
- **Start / Stop:** progress bar, elapsed time ticking every second, current phase and path, items per second.
  Stop saves nothing; closing the window while it runs asks first.

## Comparison (W4)

Two panes since 1.1.0 (mockup: the "SysStateTracer comparison window mockup" canvas).

- **Top bar:** snapshot 1 · ⇄ Swap · snapshot 2 · Refresh. Each name has **OLDER** or **NEWER** before it, by
  creation time (issue #6). Swap is instant: new ↔ deleted, the captions move with the snapshots; the open folders
  and the selection stay.
- **Left — Show all / Only changes** tabs, remembered between sessions. Only changes lists what changed and the
  folders and keys leading to it; Show all also lists what is the same in both snapshots (read from the snapshots
  only for what is opened, so the comparison stays as fast; replaces 1.0's Show unchanged, issue #4).
- **Left — the tree:** File system, Registry, Services and Environment variables with their counts, then drives,
  folders, hives and keys;
  no files or values, in larger text. The registry shows **HKCU** (your own key, `HKU\<your SID>`), HKLM and HKU
  (the other users) side by side, as in Regedit; HKU's count leaves out HKCU, and with Only changes HKU is left out
  when nothing else in it changed. Arrows open and close them; the categories show their changes as **+20 ~1 −1**
  in the change colours, folders and keys their counts in grey. New folders and keys are
  green, modified orange, deleted red; one that is the same itself but holds changes is blue; unchanged ones are in
  normal text. A folder or key shows how many changes it holds; a new or deleted one how many items it held, and
  opens like any other (its contents come from the snapshot that has it; F10, F11).
- **Left — opening and closing branches:** right-click a row → **Expand all** opens it and every folder or key
  below it; **Collapse all** closes it and everything below it. Keys, on the selected row: **\*** expand all,
  **/** collapse all, **+** open one level more, **−** close one level (the deepest open one). Expand all stops at
  250,000 rows in the tree (Show all on a whole drive has millions); the status bar then says so.
- **Right — the list:** the files of the selected folder, the values of the selected key, every service (select
  Services) or every environment variable (select Environment variables). Columns: change sign (F29), Name, then
  Size · Date modified · Attributes (· Permissions when both snapshots recorded them), Type · Data, Display name · Status · Start type, or Variables (System or User) ·
  Type · Value, from the right-hand snapshot (the left one for an item only there). Click a heading to sort,
  again to reverse; by name at first. Values that differ are highlighted; the colour runs through the row (F27).
  **Include subfolders** (remembered) also lists the files of every folder below, named from the selected folder
  (`root\office00.dll`); at most 250,000 rows.
- **Below — details:** the selected item in both snapshots side by side (size with exact bytes, date, attributes;
  type and data; display name, status, start type; System or User, type and value; for a folder or key its date and
  contents), differences
  highlighted; a value too long for one line (a Path variable) goes on up to three, broken between folders, and a longer one
  scrolls (a scroll bar beside it; mouse wheel, click or drag); values are shown whole up to 32,768 characters; each column is headed OLDER or NEWER and the snapshot name (issue #6). Its full path is in the status
  bar (F19).
- The keyboard starts in the tree when the window opens. **Tab** moves from the tree to the list, **Shift+Tab** back.
- **Status bar:** path, marks, Global Ignore list, and the colour legend: + New, ~ Modified, − Deleted, Changes
  inside, Unchanged, Changed value.
- **Environment variables** are compared only when both snapshots have them (snapshots taken before 1.3 have none);
  otherwise the category says "not in both snapshots".
- **Permissions** are compared only when both snapshots recorded them. A folder, file or key whose own permissions
  changed is Modified; the Permissions column says what changed ("Owner changed", "+ Allow · Users · Modify",
  "Inheritance disabled, +3 entries"), otherwise "Inherited" or "Own · unchanged". The details add **Owner**,
  **Inheritance** and **Own entries** (with what each entry applies to). A change made on a folder and inherited by
  its contents shows on that folder only. For a new item, Inheritance and Own entries are highlighted when it does
  not just inherit (inheritance disabled, or entries of its own); the item itself stays New.
  One exception: in folders that do not use automatic inheritance (such as `%TEMP%`), new files hold copies of the
  folder's entries that Windows does not mark as inherited. The first time the folder's permissions are written,
  Windows turns those copies into inherited entries, and each such file is reported once with its own entries removed.
- **Selection:** in either pane; Ctrl+click, Shift+click, arrow keys, Ctrl+A in the list; Tab moves between the panes.
  Right-click or the menu key for actions. Items that are unchanged or inside a whole new or deleted folder are for
  information: only Jump to works on them.

| Action | Available for | What it does |
|---|---|---|
| Jump to | one item | Explorer with the file selected, Regedit at the key, the Services console with the service selected, or Regedit at the key that holds the environment variable |
| Expand all / Collapse all | one tree row | Opens or closes the row and everything below it (also \* and /) |
| Mark as non-important (Del) | any items | Strikes them through until the window closes; left out of exports. A folder or key whose changes are then all marked is marked too. Del in the tree or the list marks the selection, or unmarks it when it is all marked |
| Add to Global Ignore list | any items | Adds rules to the local list and hides the items |
| Export to .REG | registry items, also a whole hive or the Registry row | New and modified keys and values from the newer snapshot |
| Copy to .\Files | folders and files, also a drive or the File system row | New and modified files, with a progress window and Cancel; skipped files are listed |
| Revert Value | registry keys and values | Asks "Do you want to revert to value from Snapshot X?", lists current → restored values, writes a backup to `Backups\`, then reverts |

Both panes paint only the rows on screen: with a million rows, scrolling and expanding stay within a frame.

## Preferences (W5)

**General** and **Global Ignore list** are tabs at the top, as in the comparison window; each setting group is a card
with its heading above it.

- **General:** snapshot folder (Browse…, or the program folder; it must be writable) and theme, each choice with a
  small picture of the window in that theme (F5; click the picture or the button). The theme applies live; Cancel
  restores it. **Lists:** Show grid lines and Use zebra striping (both off by default) for the snapshot list and
  the comparison window; they also apply live and Cancel restores them.
- **Global Ignore list:** the local list (`SysStateTracer.ignore.txt` beside the program) is always used; **Also use
  the shared remote list** (URL or network file, with Check now) adds its rules too (F22). Below, **Local list** and
  **Remote list** tabs (issue #7), each with its rule count and its own scroll position, above the list on a card
  (in compact rows). The local list's rules can
  be typed (Add rule…, with wildcards), edited in place (Edit…, double-click or F2), added from a folder, registry key
  or service, removed, imported and exported (F25). Comments in the file are kept. The Remote list tab shows the
  remote list's rules read-only: it is read the first time the tab is opened and again on Check now, from the
  location typed in, even when the remote list is not ticked. Edit… and Remove are off there; the add buttons and
  Import still add to the local list, and switch to its tab.

## Help / About (W6)

Version, the GitHub link, and the command-line help (the same text as `SysStateTracer.exe /?`).

## Manual test script

Run on a Windows PC with the published `SysStateTracer.exe`. It starts without a UAC prompt; click
**Restart as administrator** in the main window for complete snapshots (and once without, to see the standard-user view):

1. Start the program with no snapshots: the list says there are none. Click **New snapshot**, keep the default name, Start.
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
   Select Environment variables: change a user variable between two snapshots and check it is listed as modified.
   Tick Permissions in New Snapshot, give Users Modify on a test folder (Properties → Security), take a second
   snapshot: only that folder is Modified, its details show the new entry, and the files inside are not listed.
   In the tree, press \* on File system, then − a few times, then /; right-click a folder → Expand all.
9. Mark an item as non-important; close and reopen the comparison: the mark is gone.
10. Preferences: switch Dark/Light (all windows change at once), Cancel (it goes back); change the snapshot folder.
    Move and resize Preferences, close it and open it again: it comes back at the same place (also About and Properties).
11. In the list: F2 rename, Del delete (several at once), Enter properties (with skipped items), F5.
