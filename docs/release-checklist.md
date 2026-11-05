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
| Standard user account | Starts without a UAC prompt; the yellow bar shows; New Snapshot greys out folders and keys the user cannot read; a snapshot works; Restart as administrator asks for an administrator's password. |
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
   `SysStateTracer.exe`, `SysStateTracer.exe.sha256`, `Manual.pdf` and the notes from the changelog (with the
   exe's SHA-256). Since 1.2.2 the exe is published on its own again (1.1.0–1.2.1 had it inside
   `SysStateTracer.zip`); users unblock it in its Properties before the first start.
4. The exe is uploaded to VirusTotal and the workflow waits for the analysis (up to 30 minutes). The result
   ("VirusTotal: **0/66 Detected**" with a link to the report) is added to the release notes, and for a full
   release the badge at the top of README.md (between `<!-- virustotal:start -->` and `<!-- virustotal:end -->`)
   is updated and committed to `main` by `github-actions[bot]`. This needs the repository secret
   `VIRUSTOTAL_API_KEY` (a free VirusTotal account's API key: Settings → Secrets and variables → Actions); without
   it, or if VirusTotal does not answer in time, the release is published without a result and the run shows a
   warning. Pull before your next push: that commit is on `main`.
   To scan a published release again (the scan failed or timed out), run **Actions → virustotal → Run workflow**
   with its version: it updates the release notes and, for the latest full release, the README badge.
5. If Microsoft Defender or other engines ever flag a release, submit the exe to them as a false positive.
