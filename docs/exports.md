# Exports and actions

What the comparison window and `/compare-snap` / `/export` can produce from a comparison. All of it is in
`SysStateTracer.Core` (`Export/`, `Actions/`) and is covered by tests; the windows (Phase I) and the command
line (Phase H) only call it.

## What is exported

Exports always take **new and modified** items from the **newer** snapshot (by creation time, whatever
side it is shown on). Deleted items are never exported. A selected folder or key covers everything changed
below it, including a whole drive or hive (F3). Items marked non-important are left out together with
everything below them.

| Export | Result |
|---|---|
| Registry → .reg | Regedit 5.00 format, UTF-16 LE with BOM. A new key is written with all its subkeys and values. Strings that would not import back unchanged (embedded zero, line breaks, no terminator) are written as `hex(1)`/`hex(2)`. Values over 1 MB are not in the snapshot and are listed as not exported. Tested by importing with `reg import` and scanning again. |
| Files → .\Files | New and modified files copied from the live disk, folder structure kept: `C:\ProgramData\Test\x` → `Files\ProgramData\Test\x` on the system drive, `D:\Data\x` → `Files\_D\Data\x` on other drives (D4). Last-write time and attributes are kept. Files that are locked, gone or not readable are skipped and listed with the reason. Cancel keeps the files already copied and removes the partly copied one. |
| Restore script | `Files\Restore-Files.ps1` (F7), written with every copy: copies everything in `Files` back to where it came from — `Files\ProgramData\App\x` to `C:\ProgramData\App\x`, `Files\_D\Data\x` to `D:\Data\x` — creating missing folders and overwriting what is there. `-WhatIf` lists what it would copy. It reads the folder, so files from earlier exports into the same folder are included. Tested by deleting the originals and running it. |
| HTML report | One self-contained file: summary counts, then a collapsible tree in the window's colours (light and dark). Rows are created only when a node is opened, so large reports open quickly. |

## Revert Value

Puts registry values back to the snapshot **taken first**:

- a modified value gets its old type and data, a deleted value is recreated (with its key if needed), a new value is deleted;
- on a key it reverts the values directly in that key; on a deleted key it recreates the key's values;
- values over 1 MB cannot be reverted (their data is not in the snapshot) and are reported.

The window first asks "Do you want to revert to value from Snapshot X?". Before anything changes, a backup
`Backups\Revert_<date>_to_<snapshot>.reg` is written with the current values; values that do not exist yet
are written as deletions (`"name"=-`), so importing the backup undoes the revert.

## Jump to

- Files and folders open in Explorer with the item selected; a deleted item opens its nearest existing folder.
- Keys and values open in Regedit (via its `LastKey` setting); the current user's `HKU\<SID>` is shown as `HKEY_CURRENT_USER`. A deleted key opens its nearest existing parent.
- Services open the Services console; selecting the service in it is done by the window (Phase I).
