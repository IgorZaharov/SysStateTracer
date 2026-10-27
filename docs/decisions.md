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
| D7 | Elevation | Manifest requests administrator rights at every launch. |
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
| F1 | Global Ignore list location: Remote (URL or network share) or Local, chosen with radio buttons in Preferences; Remote falls back to Local when not reachable; default Remote = the list in this repository | Local = `SysStateTracer.ignore.txt` beside the exe (created from the built-in defaults when missing). Remote = `http(s)://` URL or `\\server\share\file.txt`, read with a timeout. Default Remote: `https://raw.githubusercontent.com/IgorZaharov/SysStateTracer/main/ignore/SysStateTracer.ignore.txt`. Replaces the "local rules + merged URL list" design of Step 15. Remote is selected by default. **Add to Global Ignore list** always writes to the Local file; while Remote is in effect the new rule applies once Local is selected or the remote list is unreachable (option A, confirmed 27 Oct 2026). | C (rework, done) + I (Preferences) |
| F2 | Application icon: small HDD on black, green and blue horizontal lines overlapping on the right like file names; small and large Explorer views | `src/SysStateTracer/SysStateTracer.ico`, 16–256 px, each size drawn separately by `tools/icon/make_icon.py`; used for the exe and every window | done |
| F3 | Export the whole tree from a top-level folder; items marked non-important are left out | Export is offered on every node of the comparison tree including the top-level ones (drives, `HKLM`, `HKU`); it covers the node's whole changed subtree and skips anything marked non-important | G (exports) |
