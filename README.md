# SysStateTracer

Portable Windows utility that takes snapshots of the file system, registry and services, and compares two snapshots to show what was created, modified or deleted in between.

> **Status:** under development. Nothing is released yet.

## Highlights (planned)

- One `SysStateTracer.exe` for Windows 10/11 x64 — no installer, nothing to install first.
- Snapshots record metadata only (names, sizes, dates, registry values, service states); file contents are never read.
- Consecutive snapshots are fast: the NTFS USN Change Journal tells SysStateTracer what changed since the last one.
- Comparison view: new items green, modified yellow, deleted red; export to `.reg`, copy changed files, revert registry values.
- Command line: `/new-snap`, `/delete-snap`, `/rename-snap`, `/compare-snap`, `/export`.

## Building

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio 2026 is optional.

```powershell
dotnet build                                     # build everything
dotnet test                                      # run tests
dotnet publish src/SysStateTracer -c Release     # single-file SysStateTracer.exe
```

The published exe is in `src/SysStateTracer/bin/Release/net10.0-windows/win-x64/publish/`.
Every push also builds it on GitHub Actions — download it from the run's **Artifacts**.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/SysStateTracer` | Windows Forms UI, command line, entry point |
| `src/SysStateTracer.Core` | Scanning, snapshot storage, comparison, exports (no UI code) |
| `tests/` | Unit tests, Windows integration tests, window tests with screenshots, benchmarks |
| `docs/` | Decisions, file format, windows, exports and actions, benchmarks, command-line reference |

## Note on snapshot files

Snapshots contain registry data, which can include sensitive settings. Treat `.stsnap` files like a registry backup and don't share them publicly.
