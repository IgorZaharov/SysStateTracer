# Snapshot benchmarks

Measured with the **benchmark** workflow (Actions → benchmark → Run workflow), which runs
`tests/SysStateTracer.Benchmarks snapshot` on fresh `windows-latest` GitHub runners: 4 cores, 16 GB,
C: and D: on Azure virtual disks. The default Global Ignore list is applied. Each snapshot runs in its own
process, so peak memory is per snapshot. "Cold" is the first snapshot after the VM starts (nothing cached);
"warm" is the same snapshot right after.

Runner contents: about 1.4 million files and folders, 553,000 registry keys, 960,000 values
(287,000 distinct, 62 MB of value data), 259 services. Snapshot file: 52 MB.

## Results (2026-10-27)

| Snapshot | Total | Files | Registry | Peak private memory | Notes |
| --- | --- | --- | --- | --- | --- |
| Cold, before memory work | 567 s | 565 s | 11 s | 1,074 MB (warm) | first measurement |
| Cold, 4 / 8 / 16 / 32 folder workers | 547 / 788 / 644 / 596 s | ≈ same | 11–17 s | 816–967 MB | worker count makes no difference |
| Cold, after memory work | 505 s | 503 s | 17 s | 670 MB | |
| Warm, after memory work | 17–33 s | 8.5–18.5 s | 15–31 s | 698 MB | parts run at the same time |
| Files only, warm | 5.8–12.6 s | | | 325 MB | |
| Registry only, warm | 10–18 s | | | 492 MB | |

Writing the file takes under 1 s; loading a whole snapshot back takes 0.2–0.4 s and uses about 240 MB.
The same snapshot on two different runners can differ by 2× in time: runner hardware varies.

## What the numbers say

- **Cold file-system scans on the runners are limited by the virtual disk, not by the app.** About
  400 folders per second whether 4 or 32 folders are listed at the same time, which matches the
  per-disk I/O limit of the runner's Azure disk. A warm scan of the same data takes 6–18 s. These
  cold numbers cannot be used to tune folder workers for real PCs.
- **Memory**: peak went from about 1 GB to about 0.7 GB after these changes:
  no copies of columns while writing; registry value data stored in 4 MB chunks; the name index no
  longer keeps one string per name; builders release their storage when the tree is built; one
  compacting garbage collection between scanning and building.
  What remains is mostly the snapshot itself (240 MB) plus the builders while the tree is built.
- **Skipped**: 733–785 registry keys that deny access even to administrators (for example
  `HKLM\SOFTWARE\Microsoft\EAPSIMMethods\...\FastReauthContext`). They are now opened with backup
  rights, like protected folders: the latest run skipped nothing and found about 9,000 more keys.

## Master file table (D10), 2026-10-27

**GitHub runner** (C: and D:, 1.4 million files and folders):

| Snapshot | Files part | Total | Peak private memory |
| --- | --- | --- | --- |
| Cold, full, automatic (file table) | 17.1 s | 18.8 s | 926 MB |
| Cold, files only, folder listing | 1,203 s | 1,204 s | 343 MB |
| Warm, files only, file table | 4.8 s | 5.8 s | 505 MB |
| Warm, files only, folder listing | 8.1 s | 8.9 s | 333 MB |

Reading the file table makes the first snapshot on the runner about **70 times faster**.

**Owner's PC** (BRIX-LEVANEV, Windows 11, 8 cores, SSD; drive C: only, 126,000 folders and 648,000 files;
Windows had much of it cached, so "first" is not fully cold):

| Snapshot | Files part | Total | Peak private memory |
| --- | --- | --- | --- |
| Files only, file table | 4.3 s | 5.2 s | 495 MB |
| Files only, folder listing, first | 7.3 s | 8.2 s | 423 MB |
| Files only, folder listing, second | 3.8 s | 4.6 s | 433 MB |
| Full (C:, 462,000 keys, 278 services), automatic | 6.7 s | 8.1 s | 768 MB |

Both methods find the same entries (differences: a few temporary files created between runs), and the
Windows test `FileTable_MatchesFolderListing` checks names, attributes, sizes and times are identical.
On a fast SSD with a warm cache folder listing is already quick; the file table is about as fast either
way, and far faster when nothing is cached or the disk is slow.

## Consecutive snapshots (Phase E), 2026-10-27

Same GitHub runner set-up; full snapshot of C:, D:, HKLM, HKU and services with `SysStateTracer.cache`.

| Snapshot | Files | Registry | Total | Peak private memory | How |
| --- | --- | --- | --- | --- | --- |
| First (cold, writes the cache) | 19.5 s | 19.4 s | 21.7 s | 830 MB | file table, all registry values read |
| Consecutive | 2.0 s | 7.0 s | 9.5 s | 957 MB | C: 13 folders read again (85 journal records); D: has no change journal, so its (tiny) file table is read; values of 562,092 of 562,331 keys reused |
| Consecutive again | 2.2 s | 6.9 s | 9.4 s | 962 MB | C: 3 folders read again (26 records) |
| Full rescan (warm) | 7.1 s | 14.5 s | 16.6 s | 865 MB | cache ignored on request |

Against the plan's targets (§8.7) for consecutive snapshots: files 1–5 s ✔, registry 3–8 s ✔.
The registry is now the slowest part: every key is still visited (only reading values is skipped), and
visiting 562,000 keys takes about 7 s. Peak memory is about 100 MB higher than a full snapshot because the
previous snapshot is in memory during the scan.

## Open question: reading the NTFS master file table directly

Listing folders one by one costs one or more random disk reads per folder. Reading the NTFS master
file table (MFT) instead reads all file records in large sequential blocks — about 1.4 GB on the
runner, roughly 1 KB per file — and rebuilds the folder tree in memory. Tools such as Everything and
WizTree work this way; on spinning disks and throttled virtual disks it is typically several times
to tens of times faster than listing folders. It needs administrator rights (which the app already
has) and works only on NTFS, so folder listing stays as the fallback for FAT32, exFAT and ReFS.
Approved as decision D10 in [decisions.md](decisions.md): whole NTFS drives are now read this way.
