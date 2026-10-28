# Global Ignore list

Rules in the Global Ignore list are skipped while taking snapshots and hidden from comparisons and
exports. The list is edited in **Preferences → Global Ignore list**, and items can be added from the
comparison window with **Add to Global Ignore list**.

## Syntax

One rule per line. Blank lines and lines starting with `#` are ignored.

| Prefix | Applies to | Example |
| --- | --- | --- |
| `FS:` | A folder or file **and everything below it** | `FS:C:\Windows\Temp` |
| `REG:` | A registry key **and everything below it** | `REG:HKLM\SOFTWARE\Vendor\Telemetry` |
| `REG:` | A single registry value: key path, then `\`, then the value name | `REG:HKLM\SOFTWARE\Vendor\LastRun` |
| `SVC:` | A service, by its key name (as shown in services.msc → Properties → Service name) | `SVC:edgeupdate` |

Wildcards:

- `*` matches any text, **including** `\`. So `FS:*\AppData\Local\Temp` matches the Temp folder of every user.
- `?` matches exactly one character other than `\`.

A `REG:` rule that names both a subkey and a value (same name, same key) leaves out both.

Matching ignores upper/lower case. A trailing `\` and `/` separators are accepted.

### Registry names

| You write | Matches |
| --- | --- |
| `HKLM`, `HKEY_LOCAL_MACHINE` | `HKLM` |
| `HKU`, `HKEY_USERS` | `HKU` |
| `HKCU`, `HKEY_CURRENT_USER` | **every** user: `HKU\*` |
| `HKCR`, `HKEY_CLASSES_ROOT` | `HKLM\SOFTWARE\Classes` |
| `HKCC`, `HKEY_CURRENT_CONFIG` | `HKLM\SYSTEM\CurrentControlSet\Hardware Profiles\Current` |
| `...\CurrentControlSet\...` | the real `ControlSet001`, `ControlSet002`, … keys that snapshots contain |

Snapshots store only `HKLM` and `HKU`; the other root keys are views of those.

## Which lists are used

**Preferences → Global Ignore list**:

- The **local** list, `SysStateTracer.ignore.txt` beside SysStateTracer.exe, is **always** used. When it does not
  exist it is created with the built-in default rules
  ([`src/SysStateTracer.Core/Ignore/default-ignore.txt`](../src/SysStateTracer.Core/Ignore/default-ignore.txt)).
  It is a plain text file: edit it in Preferences (Add rule…, Edit…, Remove, …) or in any text editor.
  **Add to Global Ignore list** in the comparison window adds to it, after showing the rules so they can be changed.
- **Also use the shared remote list** (on by default) adds the rules of a list read from an `http://` / `https://`
  address or from a file path, including a network share such as `\\server\share\SysStateTracer.ignore.txt`. The
  default address is the shared list in this repository:
  `https://raw.githubusercontent.com/IgorZaharov/SysStateTracer/main/ignore/SysStateTracer.ignore.txt`
  ([ignore/SysStateTracer.ignore.txt](../ignore/SysStateTracer.ignore.txt)).
  It is read when a snapshot or comparison starts (at most 1 MB, 10 seconds). If it cannot be read — offline, server
  or share unreachable, not a rule list — the local list is used alone and Preferences shows why. The remote list is
  never changed by SysStateTracer.

Rules from both lists apply together (F22). Untick the remote list to use the local list only.

## Default rules

The built-in rules cover temporary folders, caches, logs, registry hive files, usage-tracking keys,
and `HKLM\SAM` / `HKLM\SECURITY`. They are ordinary rules: remove any line whose changes you want to see.
