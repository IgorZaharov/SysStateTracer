# Release checklist

What has to be true before a version is published (plan Steps 55–56).

## Checked automatically on every push

| Check | Where |
|---|---|
| Build with warnings as errors; unit, Windows and window tests | `build` job (windows-latest) |
| The publish folder holds only `SysStateTracer.exe` | `build` job |
| `/?`, exit codes for wrong arguments and missing snapshots | `build` job, on the published exe |
| Snapshot → change → snapshot → compare → export → rename → delete with the published exe, on a runner where it was never run before | `clean-os` job, Windows Server 2022 (Windows 10 21H2 base) and Windows Server 2025 (Windows 11 24H2 base) |
| Microsoft Defender scan of the exe, when the runner has Defender | `clean-os` job |
| Screenshots of every window in both themes | `ui-preview` pre-release |

## Checked by hand on real Windows (§13.3)

Use the release candidate (`v1.0.0-rc.N` pre-release) and tick each row.

| Environment | Check |
|---|---|
| Windows Sandbox (fresh) | Copy the exe in, start it: it runs with nothing installed. Snapshot → change something → snapshot → compare. |
| Windows 10 22H2 x64 | Same flow; the command-line examples from `/?`; Light, Dark and Follow Windows. |
| Windows 11 25H2 x64 | Same flow; Jump to Explorer, Regedit and the Services console. |
| 100 %, 150 %, 200 % display scaling | Windows, icons and the comparison tree stay sharp and aligned. |
| Standard user account | The UAC prompt appears; declining it just doesn't start the program. |
| Microsoft Defender real-time protection on | No detection when downloading, starting and taking a snapshot. Note what SmartScreen shows on first start. |
| Real scenario (§13.4) | Install and uninstall 7-Zip between snapshots: its folder, uninstall key and file associations show as new, then deleted. Change a service's startup type: the comparison shows it. |
| Windows UI | The manual test script in [user-interface.md](user-interface.md#manual-test-script). |
| Speed on your PC | Double-click `SysStateTracer.Bench.exe` from the `bench` pre-release; it now also times loading and comparing. |

## Publishing

1. Update `CHANGELOG.md`.
2. Either tag and push (`git tag v1.0.0 && git push origin v1.0.0`, or `v1.0.0-rc.1` for a pre-release), or open
   **Actions → release → Run workflow** and enter the version (`1.0.0`, `1.0.0-rc.1`); the tag is then created on
   the latest commit of `main`.
3. The `release` workflow builds, tests, checks the version the exe reports, and creates the GitHub release with
   `SysStateTracer.exe`, `SysStateTracer.exe.sha256` and the notes from the changelog.
4. If Microsoft Defender ever flags a release, submit the exe to Microsoft as a false positive.
