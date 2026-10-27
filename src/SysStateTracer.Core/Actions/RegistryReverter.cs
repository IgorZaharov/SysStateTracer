using System.Globalization;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Actions;

public enum RevertAction
{
    /// <summary>Set the value to what the older snapshot has (also creates it, and its key, if missing).</summary>
    Restore,

    /// <summary>Delete the value: it did not exist in the older snapshot.</summary>
    Delete,
}

/// <summary>One value to revert. <see cref="Problem"/> is set when it cannot be reverted (then it is skipped).</summary>
public sealed record RevertItem(string KeyPath, string ValueName, RevertAction Action, uint Type, byte[] Data, string? Problem)
{
    /// <summary>"HKLM\SOFTWARE\Vendor\Name" ("(Default)" for the default value).</summary>
    public string DisplayPath => KeyPath + "\\" + (ValueName.Length == 0 ? "(Default)" : ValueName);
}

/// <summary>A value as it is in the live registry now.</summary>
public sealed record LiveValue(bool Exists, uint Type, byte[] Data);

public sealed record RevertOutcome(RevertItem Item, string? Error);

public sealed record RevertResult(string BackupPath, IReadOnlyList<RevertOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Error is null);
}

/// <summary>
/// Revert Value (plan 2.8): puts registry values back to the older snapshot, by creation time, whatever
/// the display order. A modified value gets its old type and data, a new value is deleted, a deleted value is
/// recreated. On a key it reverts the values directly in that key. Before anything changes, a .reg backup of
/// the current values is written to Backups\ (values that do not exist yet are recorded as deletions, so
/// importing the backup undoes the revert). The window asks "Do you want to revert to value from Snapshot X?" first.
/// </summary>
public static class RegistryReverter
{
    /// <summary>The snapshot values are restored from: the one taken first.</summary>
    public static Snapshot Target(DiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return diff.Newer.Meta.StartedAt < diff.Older.Meta.StartedAt ? diff.Newer : diff.Older;
    }

    public static List<RevertItem> Plan(DiffResult diff, IEnumerable<int> registryNodes)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(registryNodes);
        var target = Target(diff);
        var targetIsOlder = ReferenceEquals(target, diff.Older);
        var other = targetIsOlder ? diff.Newer : diff.Older;
        var tree = diff.Registry;
        var items = new List<RevertItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in registryNodes)
        {
            if (tree.Kind(node) == DiffItemKind.Value)
            {
                AddValueNode(node);
            }
            else if (tree.Kind(node) == DiffItemKind.Key)
            {
                var inTarget = targetIsOlder ? tree.OlderIndex(node) : tree.NewerIndex(node);
                var inOther = targetIsOlder ? tree.NewerIndex(node) : tree.OlderIndex(node);
                if (inTarget >= 0 && inOther >= 0)
                {
                    foreach (var child in tree.Children(node))
                    {
                        if (tree.Kind(child) == DiffItemKind.Value)
                        {
                            AddValueNode(child);
                        }
                    }
                }
                else if (inTarget >= 0)
                {
                    // The key is gone now: recreate it with its values.
                    var path = target.Registry.GetFullPath(inTarget, target.Names);
                    var first = target.Registry.FirstValue[inTarget];
                    for (var v = first; v < first + target.Registry.KeyValueCount[inTarget]; v++)
                    {
                        Add(Restore(path, target, v));
                    }
                }
                else if (inOther >= 0)
                {
                    // The key is new: remove the values directly in it.
                    var path = other.Registry.GetFullPath(inOther, other.Names);
                    var first = other.Registry.FirstValue[inOther];
                    for (var v = first; v < first + other.Registry.KeyValueCount[inOther]; v++)
                    {
                        Add(new RevertItem(path, other.Names.GetString(other.Registry.ValueName[v]), RevertAction.Delete, 0, [], null));
                    }
                }
            }
        }

        return items;

        void AddValueNode(int node)
        {
            var key = tree.Parent(node);
            var inTarget = targetIsOlder ? tree.OlderIndex(node) : tree.NewerIndex(node);
            var keyIndex = targetIsOlder ? tree.OlderIndex(key) : tree.NewerIndex(key);
            var path = target.Registry.GetFullPath(keyIndex, target.Names);
            if (inTarget >= 0)
            {
                Add(Restore(path, target, inTarget));
            }
            else
            {
                var inOther = targetIsOlder ? tree.NewerIndex(node) : tree.OlderIndex(node);
                Add(new RevertItem(path, other.Names.GetString(other.Registry.ValueName[inOther]), RevertAction.Delete, 0, [], null));
            }
        }

        void Add(RevertItem item)
        {
            if (seen.Add(item.DisplayPath))
            {
                items.Add(item);
            }
        }
    }

    /// <summary>The value as it is in the live registry now (for the confirmation list and the backup).</summary>
    public static LiveValue ReadCurrent(RevertItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return LiveRegistry.Read(item.KeyPath, item.ValueName);
    }

    /// <summary>Writes the backup to <paramref name="backupFolder"/>, then reverts every item without a problem.</summary>
    public static RevertResult Apply(IReadOnlyList<RevertItem> items, string backupFolder, string snapshotName)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);

        var backup = new RegFile();
        foreach (var item in items.Where(i => i.Problem is null))
        {
            var current = ReadCurrent(item);
            if (current.Exists)
            {
                backup.AddValue(item.KeyPath, item.ValueName, current.Type, current.Data);
            }
            else
            {
                backup.AddValueDeletion(item.KeyPath, item.ValueName);
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HH-mm-ss", CultureInfo.InvariantCulture);
        var safeName = string.Concat(snapshotName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var backupPath = Path.Combine(backupFolder, $"Revert_{stamp}_to_{safeName}.reg");
        backup.Save(backupPath);

        var outcomes = new List<RevertOutcome>(items.Count);
        foreach (var item in items)
        {
            if (item.Problem is not null)
            {
                outcomes.Add(new RevertOutcome(item, item.Problem));
                continue;
            }

            var error = item.Action == RevertAction.Restore
                ? LiveRegistry.Write(item.KeyPath, item.ValueName, item.Type, item.Data)
                : LiveRegistry.Delete(item.KeyPath, item.ValueName);
            outcomes.Add(new RevertOutcome(item, error == 0 ? null : Win32Errors.Describe(error)));
        }

        return new RevertResult(backupPath, outcomes);
    }

    private static RevertItem Restore(string keyPath, Snapshot target, int value)
    {
        var reg = target.Registry;
        var name = target.Names.GetString(reg.ValueName[value]);
        return reg.IsValueDataStored(value)
            ? new RevertItem(keyPath, name, RevertAction.Restore, reg.ValueDataType[value], reg.GetValueData(value).ToArray(), null)
            : new RevertItem(keyPath, name, RevertAction.Restore, reg.ValueDataType[value], [], "larger than 1 MB: its data is not in the snapshot");
    }
}

/// <summary>Raw reads and writes of the live registry (64-bit view), by snapshot path ("HKLM\…", "HKU\…").</summary>
internal static class LiveRegistry
{
    private const uint Wow64 = NativeMethods.KeyWow6464Key;

    public static unsafe LiveValue Read(string keyPath, string name)
    {
        var (hive, relative) = Split(keyPath);
        if (hive == 0 || NativeMethods.RegOpenKeyEx(hive, relative, 0, NativeMethods.KeyQueryValue | Wow64, out var key) != 0)
        {
            return new LiveValue(false, 0, []);
        }

        using (key)
        {
            uint length = 0;
            var error = NativeMethods.RegQueryValueEx(key, name, 0, out var type, null, ref length);
            if (error != 0)
            {
                return new LiveValue(false, 0, []);
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var data = new byte[length];
                fixed (byte* p = data)
                {
                    error = NativeMethods.RegQueryValueEx(key, name, 0, out type, p, ref length);
                }

                if (error == 0)
                {
                    return new LiveValue(true, type, data.AsSpan(0, (int)length).ToArray());
                }

                if (error != NativeMethods.ErrorMoreData)
                {
                    break;
                }
            }

            return new LiveValue(false, 0, []);
        }
    }

    public static unsafe int Write(string keyPath, string name, uint type, byte[] data)
    {
        var (hive, relative) = Split(keyPath);
        if (hive == 0)
        {
            return Win32Errors.FileNotFound;
        }

        var error = NativeMethods.RegCreateKeyEx(hive, relative, 0, 0, 0, NativeMethods.KeySetValue | Wow64, 0, out var key, out _);
        using (key)
        {
            if (error != 0)
            {
                return error;
            }

            fixed (byte* p = data)
            {
                return NativeMethods.RegSetValueEx(key, name, 0, type, p, (uint)data.Length);
            }
        }
    }

    public static int Delete(string keyPath, string name)
    {
        var (hive, relative) = Split(keyPath);
        if (hive == 0 || NativeMethods.RegOpenKeyEx(hive, relative, 0, NativeMethods.KeySetValue | Wow64, out var key) != 0)
        {
            return 0; // the key is gone, so the value is gone too
        }

        using (key)
        {
            var error = NativeMethods.RegDeleteValue(key, name);
            return error == Win32Errors.FileNotFound ? 0 : error;
        }
    }

    private static (nint Hive, string Relative) Split(string path)
    {
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        var head = slash < 0 ? path : path[..slash];
        var relative = slash < 0 ? string.Empty : path[(slash + 1)..];
        var hive = head.ToUpperInvariant() switch
        {
            "HKLM" => NativeMethods.HkeyLocalMachine,
            "HKU" => NativeMethods.HkeyUsers,
            _ => (nint)0,
        };
        return (hive, relative);
    }
}
