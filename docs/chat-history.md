# SysStateTracer — project handover

Summary of the work done in the Claude chat (27 Oct – 2 Nov 2026), so the project can continue in Claude Code.
State at handover: **v1.1.0 released** (29 Oct 2026, as `SysStateTracer.zip`), `main` green on CI, no uncommitted
code. Open work is GitHub issues #6–#9 (section 5).

## 1. Goal

A portable Windows 10/11 x64 tool that takes **snapshots** of the file system, registry and services and
**compares** two of them, to show what was created, modified or deleted in between — typically what an installer
really did. Owner: Igor Zaharov (application packaging background). Inspired by SysTracer (Blue Project Software).

Targets: fast (whole drives in seconds), one exe with nothing to install, readable comparisons, exports that help
packaging work (.reg export, copy changed files, revert registry values, HTML report, command line).

Where things are:

| What | Where |
| --- | --- |
| Repository | https://github.com/IgorZaharov/SysStateTracer (`main` only, no branches) |
| Releases | https://github.com/IgorZaharov/SysStateTracer/releases (latest: v1.1.0) |
| Screenshots of every window, light and dark, and the latest Manual.pdf | `ui-preview` pre-release, updated by CI on every push |
| Benchmark tool for real PCs | `bench` pre-release (`SysStateTracer.Bench.exe`, see `docs/benchmarks.md`) |
| Original architecture & build plan (§ numbers and "Steps" in code comments refer to it) | claude.ai document "SysStateTracer — Architecture & Build Plan" (https://claude.ai/artifact/HhpT4srQLj3DJhvRbMbhWL); the decisions it led to are in `docs/decisions.md` |
| Mockup of the 1.1.0 comparison window | claude.ai canvas "SysStateTracer comparison window mockup" (https://claude.ai/artifact/GYaFTaei5MLMgVGJc12AWd) |
| Feature requests and bugs (numbered, with the version that delivered them) | `docs/featurerequest.md` (items 1–36), then GitHub Issues |

## 2. Architecture

.NET 10 (C# 14), Windows Forms, **self-contained single-file** `SysStateTracer.exe` (win-x64). Two projects:

- `src/SysStateTracer.Core` — everything without UI:
  - `Scanning/` — `SnapshotOrchestrator` runs file system, registry and services scans in parallel.
    - `Fs/FileSystemScanner*.cs`: whole NTFS drives are read from the **master file table** (`Ntfs/MftIndex`);
      other roots by folder listing (`GetFileInformationByHandleEx`).
    - **Incremental snapshots**: the previous snapshot is cached (`SysStateTracer.cache` beside the exe) with each
      drive's change-journal checkpoint; the next snapshot copies the previous tree and re-reads only what the
      **USN journal** reports (`FileSystemScanner.Incremental.cs`, `PreviousSnapshot.cs`). Registry keys are reused
      by last-write time.
    - Folder **links** (junctions, symlinks) leading outside the scan are followed and recorded under the local
      path (`FileSystemScanner.Links.cs`, issue #2). Volumes **mounted in a folder** are read through the volume.
    - **FSLogix profiles** (`FileSystemScanner.Volumes.cs`): the FSLogix filter driver redirects
      `C:\Users\<name>` to a VHDX and hides it from every normal call (serial, path, file ids). Only C:'s MFT shows
      the truth (an empty folder). Where FSLogix is installed (`frxdrv`/`frxsvc` service keys) and elevated, each
      profile folder's listing is compared with its MFT record; a mismatch means "virtualized" → the folder is read
      through Windows in full on every snapshot (like a link, no file ids). Verified on the owner's VM.
  - `Model/` — column-oriented trees: `FsTree`, `RegTree`, `ServiceTable`, shared `NameTable` (interned names).
  - `Storage/` — the `.stsnap` binary format (`docs/format.md`): header, compressed column sections, JSON meta.
  - `Comparison/` — `SnapshotComparer` walks both sorted trees, skips equal subtrees by digest, produces a
    `DiffTree` per area (only changes and their parents; a whole new/deleted folder or key is one node).
    `Swap()` exchanges sides without recomputing. `DiffActions` = context-menu availability rules.
  - `Ignore/` — Global Ignore list: rules `FS:`, `REG:`, `SVC:` with `*` (crosses `\`) and `?`; local file
    `SysStateTracer.ignore.txt` beside the exe plus an optional shared remote list (URL or UNC), combined.
    Built-in defaults: `src/SysStateTracer.Core/Ignore/default-ignore.txt`; the shared list: `ignore/SysStateTracer.ignore.txt`.
  - `Export/` — `.reg` export, copy to `.\Files`, HTML report (`Results.html`), `RegistryReverter` (Revert Value).
  - `CommandLine/` — `CliRunner` (`/?`, `/snapshot`, `/compare-snap`, `/export` …, see `docs/command-line.md`).
  - `Config/` — `AppPaths` (data beside the exe, fallback when the folder is not writable), `AppSettings`
    (JSON), `SettingsStore`, `FriendlyErrors`.
- `src/SysStateTracer` — the WinForms app. All controls are drawn by the app (`Controls/`, `Theming/`): light,
  dark, follow-Windows themes (`ThemeManager`, `Palette`), Segoe Fluent glyphs (`Glyphs`).
  - `MainForm` (snapshot list; drag one onto another to compare), `NewSnapshotForm` (scope tree, progress),
    `PropertiesForm`, `PreferencesForm` (General: folder, theme, grid lines/zebra; Global Ignore list),
    `AboutForm`, dialogs.
  - **Comparison window (1.1.0, two panes)**: `ComparisonForm` + `ComparisonData` (shared logic: statuses,
    children in Show all / Show only changes, names, paths, details) + `ComparisonTreeModel` (folders/keys) +
    `ComparisonListModel` (files/values/services, sorting, Include subfolders, max 250,000 rows) +
    `Controls/VirtualTreeGrid` (virtual, paints only visible rows; tree lines, sort arrows, highlighted cells),
    `DetailsPanel`, `ColorLegend`, `ThemedSplitContainer`. Rows are identified by their **index in the older and
    newer snapshot**, so Swap/Refresh/new ignore rules keep the view (restored by path keys).
- Tests: `tests/SysStateTracer.Core.Tests` (pure logic), `tests/SysStateTracer.WindowsTests` (real scans, USN
  journal, registry, diskpart VHD mount — need Windows, some need admin), `tests/SysStateTracer.UiTests` (windows on
  an STA thread, screenshots), `tests/SysStateTracer.Benchmarks`. About 400 tests.
- CI (`.github/workflows`): `build.yml` on every push to `main` (build, tests, publish, smoke test on clean
  Windows Server 2022/2025 runners, screenshots + Manual.pdf to `ui-preview`); `release.yml` (manual dispatch with
  a version → tag, release with `SysStateTracer.zip`, `.zip.sha256`, `Manual.pdf`, notes from `CHANGELOG.md`);
  `repackage.yml` (move an old release's exe into a zip, rewrite its notes); `benchmark.yml`.
- Manual: `tools/manual/make_manual.py` (ReportLab) builds `Manual.pdf` from the CI screenshots and the `/?` text.

## 3. Key decisions (full log: `docs/decisions.md`)

- **D1** .NET 10 self-contained single file; Windows 10 22H2 and Windows 11 are the test targets.
- **D8** Unsigned; every release publishes SHA-256 checksums. **Since 1.1.0 the download is
  `SysStateTracer.zip`** (fewer browser warnings; only the zip needs Unblock, then Extract All). No bare exe.
- **D10** Whole NTFS drives from the MFT; listing elsewhere. Incremental via the USN journal.
- **F9** No UAC prompt at start; standard users work; "Restart as administrator" for complete snapshots;
  snapshots record which rights they were taken with.
- `HKLM\SAM` and `HKLM\SECURITY` are excluded by default; **snapshots contain sensitive registry data** — never
  share them publicly.
- Data lives **beside the exe** (settings, ignore list, cache, log `SysStateTracer.log`, `Files\`, `Backups\`).
- Comparison: 1.0 had one tree with optional "Show unchanged" (issue #4); **1.1.0 replaced it** with two panes and
  Show all / Show only changes (remembered). Colours: new green, modified **orange** (dark theme used yellow before
  1.1.0), deleted red, "changes inside" **blue**, unchanged normal text; legend in the status bar.
- Windows are at most **580 px tall** at 100 % (F14). No hover tool tips in the comparison (F19): the status bar
  shows the selected item's path.
- Revert Value always restores the **older** snapshot's value, after writing a `.reg` backup to `Backups\`.

## 4. What is finished

- **v1.0.0** (28 Oct 2026, after rc.1–rc.3): everything in plan Phases A–J; feature requests 1–29 in
  `docs/featurerequest.md`; issues #2–#5 (folder links, HTML report depth, Show unchanged, Preferences width).
- **v1.1.0** (29 Oct 2026): two-pane comparison window (requests 30–35), grid lines / zebra striping
  (Preferences → General → Lists), FSLogix profile fix (36), mounted-volume folders, New Snapshot thread fixes,
  Properties long lines wrap; README icon; download as zip (v1.1.0 repackaged on 29 Oct).
- Since 1.1.0 the owner pushed README edits (SysTracer credit line) and many shared ignore-list rules.

## 5. What is open

GitHub issues (created 29 Oct 2026, all open, no comments yet):

| # | Type | What is asked | Notes for the implementer |
| --- | --- | --- | --- |
| 6 | Feature | Comparison window, bottom (details) pane: an **OLDER / NEWER** caption before each snapshot name, by creation time. | **Implemented on `main` (Unreleased)**, in the details headers and the top bar; captions follow Swap. Waiting for the owner to try it and close the issue. |
| 7 | Feature | Preferences → Global Ignore list: view the **remote list's rules read-only**. Rename "Local list" to a Local / Remote selector (like browser tabs), remember the scroll position per tab; buttons keep adding to the local list; "Check now" also refreshes the Remote tab. | **Implemented on `main` (Unreleased).** Two `ThemedListView`s (each keeps its scroll); the remote one is read on first open and on Check now, even when the remote list is unticked; Edit/Remove disabled there; adding switches to the Local tab. Waiting for the owner to try it and close the issue. |
| 8 | Feature | `/export` and `/compare-snap`: **filters** (`/registry:"HKCU","HKLM\Software"`, `/files:"C:\Program Files\Test"`) and **`--output:`** location; same syntax for both; verbose output and error handling; examples in About and the manual. | Switch syntax is "TBD" in the issue: agree it with the owner first. `CliRunner`, `CliHelp`, `docs/command-line.md`, `make_manual.py`, `CommandLineTests`. Export already takes node lists (`DiffActions.Nodes`). |
| 9 | Bug | **Memory**: ~79 MB at start, ~500 MB after a few snapshots and comparisons, not released. Test: idle → compare two snapshots → close window → check again. | Not investigated yet. Candidates: comparison windows or their snapshots/`DiffResult` kept alive (event handlers such as `ThemeManager.Changed`, `WindowLayout`; `MainForm` itself keeps only `SnapshotInfo` metadata), the snapshot cache and previous snapshot loaded for incremental scans, the MFT index kept in `_preloadedTables` if a scan fails, large arrays on the LOH that the GC returns to Windows late (try `GC.Collect` with `GCSettings.LargeObjectHeapCompactionMode` after a window closes; no GC settings are set in the projects today, so `GCConserveMemory`, workstation non-concurrent GC or a `GCHeapHardLimit` are options to measure), and the NameTable growth. Measure first (dotnet-counters / a memory test in UiTests). |

Other known loose ends (not filed as issues):

- The 1.1.0 two-pane comparison window was tested on test data only; the owner has not yet tried **Show all on a
  whole C:\\** or **Include subfolders on a large folder** on real snapshots.
- README/manual "Unblock" screenshot (`docs/images/unblock.png`) shows the exe's Properties, not the zip's (same
  dialog; the owner may send a new one).
- `CopyProgressForm` thread guard: done (Unreleased), same `InvokeRequired` pattern as `NewSnapshotForm`.
- CI warns that `actions/checkout@v4`, `setup-dotnet@v4`, `upload-artifact@v4` target Node 20 (deprecated);
  move to newer major versions when available.
- Old links to `releases/latest/download/SysStateTracer.exe` return 404. They are only in the README of the `v1.0.0`
  tag (the 1.0.0 manual has no download links); everything current links to `releases/latest/download/SysStateTracer.zip`.
  The owner chose (3 Nov 2026) to keep releases zip-only rather than attach the bare exe again; v1.0.0 still has its exe.

## 6. Conventions we agreed on

Working with the owner:

- Work goes **straight to `main`** with descriptive commit messages; no PRs. The owner also pushes to `main`
  (README, `docs/featurerequest.md`, `ignore/SysStateTracer.ignore.txt`): **always fetch and rebase before pushing**.
- Commits made by Claude end with the attribution lines:
  `Co-Authored-By: Claude …` and a `Claude-Session:` link (Claude Code adds its own trailer).
- **Never run anything on the owner's PC or VM without asking.** When something must be checked there, give the
  exact commands and ask for the output (as done for the FSLogix investigation).
- New requests arrive in `docs/featurerequest.md` (numbered) or as GitHub issues. When done, mark them
  **`[DONE in vX.Y.Z]`** (real tag form, e.g. `v1.0.0-rc.2`); comment on issues; closing them is the owner's call
  (ask).
- Before larger UI work: describe the effect, list decisions for the owner, make a mockup when it helps; build
  after the owner agrees. Evaluate requests honestly (cost, risks), ask only real decisions.
- **Releases**: date the `CHANGELOG.md` section (`## [X.Y.Z] — YYYY-MM-DD`; Unreleased until then), bump
  `VersionPrefix` in `Directory.Build.props`, wait for green CI, then dispatch `release.yml` with the version.
  Pre-releases as `X.Y.Z-rc.N`. Offer a release candidate when new code has not been tried on real data; the
  owner decides. After a release, check: assets, `releases/latest`, SHA-256 of the downloaded files, manual
  version label.
- Every user-visible change updates: `CHANGELOG.md`, `docs/user-interface.md` (or `command-line.md`,
  `ignore-list.md`), `docs/decisions.md` for decisions, `tools/manual/make_manual.py` (manual text), and the
  README screenshots in `docs/images/` (copied from the `ui-preview` pre-release after CI) when windows change.
  Screenshots come from `tests/SysStateTracer.UiTests/ScreenshotTests.cs`: add a screenshot there for new windows.
- Writing style for UI text, docs and the manual: plain, short sentences, one idea each (Simplified Technical
  English in spirit); name things as the UI does.

Code:

- `TreatWarningsAsErrors` with the latest analyzers: the build fails on any warning (xUnit analyzers too, e.g.
  use `Assert.Contains/Single` instead of `Assert.True(x.Any(…))`).
- Glyph characters (Unicode private-use area, e.g. Segoe Fluent icons) are always written **escaped** in source
  (`"\uE8B7"`), never as raw characters; no stray control characters in files.
- Windows Forms: never touch controls off the UI thread (`InvokeRequired` → `BeginInvoke`); guard against
  disposed windows in async continuations.
- Prefer one class per concern with XML doc comments that say *why*; reference the request or issue (F10, issue
  #2) in comments where it explains behaviour.
- Tests: Core logic in Core.Tests; anything needing Windows APIs in WindowsTests (skip silently when not
  elevated where admin is needed); window behaviour in UiTests. Timing tests allow for slow shared CI runners.

Tooling notes (from the cloud chat sandbox; may not apply in Claude Code on the owner's PC):

- CI was the compiler (no local .NET SDK in the sandbox). Wait for CI by commit:
  `gh api "repos/IgorZaharov/SysStateTracer/actions/runs?head_sha=<full sha>"`; read failures from
  `gh api repos/IgorZaharov/SysStateTracer/check-runs/<job id>/annotations` (test-report lines and crash stacks
  are annotated).
- Dispatch a release: `gh api -X POST repos/IgorZaharov/SysStateTracer/actions/workflows/release.yml/dispatches -f ref=main -f 'inputs[version]=1.2.0'`.
- Download release or `ui-preview` assets: `gh api -H "Accept: application/octet-stream" repos/IgorZaharov/SysStateTracer/releases/assets/<id> > file`.
- The sandbox could not upload release assets or edit releases (hence `repackage.yml`); with a normal `gh` login
  `gh release upload/edit` work directly.
- With the .NET 10 SDK on Windows: `dotnet build SysStateTracer.slnx -c Release` and
  `dotnet test SysStateTracer.slnx -c Release`; publish:
  `dotnet publish src/SysStateTracer/SysStateTracer.csproj -c Release -o publish`.

## 7. Next steps (suggested order)

1. Try 1.1.0 on real snapshots with the owner (Show all on `C:\`, Include subfolders, HKU) and fix what is slow.
2. Issue **#9** memory: reproduce with a measurement, find what stays referenced after the comparison window
   closes, fix, add a regression test if practical.
3. Issue **#6** OLDER / NEWER captions (small, quick win).
4. Issue **#7** remote list tab in Preferences.
5. Issue **#8** command-line filters and `--output`: agree the switch syntax with the owner, then implement with
   tests, help text, About, manual.
6. Housekeeping: zip Unblock screenshot, GitHub Actions versions.
7. Release 1.2.0 (rc first if the memory or comparison changes are large).
