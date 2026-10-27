# Snapshot file format (`.stsnap`, version 1.0)

A snapshot is one self-contained binary file. It is always a full snapshot, never a delta, so any
snapshot can be renamed, deleted or copied to another PC without affecting the others.

All integers are **little-endian**. Hashes are **XxHash3, 64-bit, seed 0**. Text is UTF-16LE
(names and paths) or UTF-8 JSON (metadata). Implementation: `src/SysStateTracer.Core/Storage`.

## Layout

```
+---------------------------+  offset 0
| Header (512 bytes)        |
+---------------------------+  offset 512
| Section blocks            |  compressed column data, in section-table order
+---------------------------+  Header.TableOffset
| Section table             |
+---------------------------+  Header.MetaOffset
| Meta (UTF-8 JSON)         |
+---------------------------+  Header.FileLength
```

The writer writes everything to `<name>.stsnap.tmp`, writes the header last, flushes to disk, then
renames the file. A `.stsnap` file therefore only exists once it is complete.

## Header (512 bytes)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 8 | Magic | `53 54 53 4E 41 50 0A 1A` = "STSNAP" LF Ctrl-Z |
| 8 | 2 | Major version | 1. Readers reject other majors. |
| 10 | 2 | Minor version | 0. Newer minors only add sections; readers ignore unknown sections. |
| 12 | 4 | Header size | 512 |
| 16 | 8 | File length | Must equal the actual file length (detects truncated copies). |
| 24 | 8 | Section table offset | |
| 32 | 4 | Section table length | Bytes |
| 36 | 4 | Section count | |
| 40 | 8 | Section table hash | XxHash3 of the table bytes |
| 48 | 8 | Meta offset | |
| 56 | 4 | Meta length | Bytes, at most 16 MiB |
| 60 | 4 | Block size | 1 048 576 (1 MiB) |
| 64 | 8 | Created (UTC ticks) | `Meta.startedAt` in .NET ticks, for sorting without parsing JSON |
| 72 | 8 | Meta hash | XxHash3 of the meta bytes |
| 80 | 424 | Reserved | Zero |
| 504 | 8 | Header hash | XxHash3 of bytes 0–503 |

Listing a folder of snapshots reads only the header and the meta of each file.

## Section table

A sequence of `SectionCount` entries. Each entry is a 16-byte section record followed by its block records.

Section record (16 bytes):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | Section kind (see below) |
| 2 | 2 | Element size in bytes (1, 2, 4 or 8) |
| 4 | 4 | Block count |
| 8 | 8 | Raw (uncompressed) length in bytes = element count × element size |

Block record (32 bytes):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | File offset of the stored bytes |
| 8 | 4 | Stored length |
| 12 | 4 | Raw length (≤ 1 MiB; all blocks but the last are exactly 1 MiB) |
| 16 | 1 | Codec: 0 = stored as-is, 1 = Brotli |
| 17 | 7 | Reserved, zero |
| 24 | 8 | XxHash3 of the stored bytes |

A section's raw bytes are the concatenation of its blocks' raw bytes. Blocks are independent, so
readers decompress them in parallel straight into the destination array. A block that does not
shrink under Brotli is stored as-is.

## Sections

Every section is one column: an array of fixed-size elements. Arrays are indexed by entry number.

### Names

| Kind | Name | Element | Content |
| --- | --- | --- | --- |
| 1 | NameChars | char (2) | All distinct names, concatenated |
| 2 | NameOffsets | int32 | Count + 1 offsets into NameChars; name *i* is `[off[i], off[i+1])` |

All other sections refer to names by index. Names are case-preserving.

### Trees (file system and registry keys)

Entries are stored in **depth-first pre-order**; siblings are sorted by name using
case-insensitive ordinal comparison, ties broken by case-sensitive ordinal comparison.
With `Descendants[i]`, the subtree of entry *i* is exactly entries *i+1 … i+Descendants[i]*.
Parents are stored as **ParentDelta = i − parent** (0 for a root), which compresses to almost nothing.

| Kind | Name | Element | Content |
| --- | --- | --- | --- |
| 10 | FsParentDelta | int32 | 0 = drive root (named e.g. `C:`) |
| 11 | FsName | int32 | Name index |
| 12 | FsAttributes | uint32 | Win32 file attributes (0x10 = directory) |
| 13 | FsSize | int64 | Bytes; 0 for folders |
| 14 | FsLastWrite | int64 | FILETIME (UTC) |
| 15 | FsDescendants | int32 | Entries below |
| 16 | FsChildFolders | int32 | Direct sub-folders |
| 17 | FsChildFiles | int32 | Direct files |
| 18 | FsDigest | uint64 | Subtree digest (below) |
| 19 | FsFileId | int64 | NTFS file reference number (record number, sequence number in the top 16 bits). **SysStateTracer.cache only.** |
| 30 | RegParentDelta | int32 | 0 = root (`HKLM`, `HKU`) |
| 31 | RegName | int32 | Name index |
| 32 | RegLastWrite | int64 | Key last-write time, FILETIME (UTC) |
| 33 | RegFirstValue | int32 | Index of the key's first value |
| 34 | RegValueCount | int32 | Values directly in the key |
| 35 | RegDescendants | int32 | Keys below |
| 36 | RegDigest | uint64 | Subtree digest (below) |

### Registry values and data

A key's values are contiguous (`RegFirstValue`, `RegValueCount`) and sorted like names.
The default value has the empty name.

| Kind | Name | Element | Content |
| --- | --- | --- | --- |
| 40 | ValName | int32 | Name index |
| 41 | ValType | uint32 | REG_* type (1 = SZ, 2 = EXPAND_SZ, 3 = BINARY, 4 = DWORD, 7 = MULTI_SZ, 11 = QWORD, …) |
| 42 | ValDataHash | uint64 | XxHash3 of the data |
| 43 | ValBlob | int32 | Blob index, or −1 when the data is larger than 1 MiB and not stored |
| 44 | ValDataLength | int32 | Data length in bytes |
| 50 | BlobOffsets | int64 | Count + 1 offsets into BlobData |
| 51 | BlobData | byte | Distinct value data, de-duplicated by content |

### Services

Sorted by service name.

| Kind | Name | Element | Content |
| --- | --- | --- | --- |
| 60 | SvcName | int32 | Name index (service key name) |
| 61 | SvcDisplayName | int32 | Name index |
| 62 | SvcType | uint32 | SERVICE_* type |
| 63 | SvcState | uint32 | SERVICE_* state (1 = stopped, 4 = running, …) |
| 64 | SvcStartType | uint32 | 0 boot, 1 system, 2 automatic, 3 manual, 4 disabled |
| 65 | SvcFlags | byte | bit 0 = delayed auto-start, bit 1 = trigger start |

### Skipped items

Paths that could not be read, with their Win32 error code.

| Kind | Name | Element | Content |
| --- | --- | --- | --- |
| 70 | SkippedChars | char (2) | Paths, concatenated |
| 71 | SkippedOffsets | int32 | Count + 1 offsets |
| 72 | SkippedErrors | int32 | Win32 error per path |
| 80 | CacheInfo | byte | UTF-8 JSON: computer, roots, ignore-list fingerprint, per-volume change-journal id and checkpoint. **SysStateTracer.cache only.** |

`SysStateTracer.cache` uses this same format: the last snapshot taken on the computer plus sections 19 and 80,
which let the next snapshot read only what changed (plan §7.8, §8.5). Readers ignore sections they do not know.

## Subtree digests

Each tree entry carries a 64-bit XxHash3 digest of itself and everything below it, so a comparison
can prove two subtrees equal without reading them. The digest of entry *i* hashes, in order:

- its name as `int32 length` + UTF-16LE characters;
- **file system:** `uint32 attributes`, `int64 size`, `int64 lastWrite`;
- **registry:** for each of the key's values in stored order: name (as above), `uint32 type`,
  `uint64 dataHash`, `int32 dataLength`. The key's own last-write time is **not** included, so a
  key rewritten with identical values is not reported as changed;
- the `uint64` digests of its direct children in stored order.

## Meta (UTF-8 JSON)

camelCase property names, enums as strings. Unknown properties are ignored.

```json
{
  "name": "20261027_14-05-12_WS-0142",
  "startedAt": "2026-10-27T14:05:12.000+02:00",
  "completedAt": "2026-10-27T14:05:21.000+02:00",
  "computerName": "WS-0142",
  "userName": "admin",
  "osEdition": "Windows 11 Pro 25H2",
  "osVersion": "10.0.26200.6899",
  "appVersion": "0.1.0+81c5413",
  "mode": "Incremental",
  "modeReason": null,
  "scope": ["C:\\", "HKLM", "HKU", "Services"],
  "fileSystemMethods": ["C:\\: master file table"],
  "ignoreRuleCount": 12,
  "timings": { "fileSystemMs": 2100, "registryMs": 5300, "servicesMs": 400, "writeMs": 1900, "totalMs": 8700 },
  "counts": { "folders": 310000, "files": 1190000, "registryKeys": 820000, "registryValues": 1450000, "services": 290, "skipped": 37 }
}
```

The writer fills `counts` and `timings.writeMs`; the snapshot orchestrator fills the rest.

## Versioning rules

- **Minor** version changes may add new section kinds or new meta properties. Older readers ignore them.
- **Major** version changes anything else. Readers refuse other major versions with a message
  asking the user to update SysStateTracer.
- Section kind numbers are never reused.
