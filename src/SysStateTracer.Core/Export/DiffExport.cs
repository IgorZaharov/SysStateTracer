using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Export;

/// <summary>A file to copy to .\Files: the live file, where it goes, and its size in the newer snapshot.</summary>
public sealed record FileToCopy(string Source, string RelativeTarget, long Size);

/// <summary>
/// What an export covers, from selected comparison items (plan §10.6, 2.6, 2.9, F3): new and modified
/// items as found by the comparison, taken from the newer snapshot. Deleted items are not exported.
/// A selected folder or key covers everything below it, including a whole top-level drive or hive;
/// items marked non-important are left out together with everything below them. Global Ignore rules
/// (<see cref="DiffResult.Ignore"/>) are applied inside new folders and keys too, which the comparison
/// keeps as one node each.
/// </summary>
public static class DiffExport
{
    /// <summary>Registry keys and values for a .reg file. Values over 1 MB (not stored) are listed in <c>NotStored</c>.</summary>
    public static (RegFile File, List<string> NotStored) Registry(DiffResult diff, IEnumerable<int> nodes, ImportanceMarks? marks = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(nodes);
        var file = new RegFile();
        var notStored = new List<string>();
        var tree = diff.Registry;
        var newer = diff.Newer;
        var done = new HashSet<int>();

        foreach (var node in nodes)
        {
            if (marks?.IsMarkedOrInside(diff, new DiffItem(DiffArea.Registry, node)) == true)
            {
                continue;
            }

            Walk(node);
        }

        return (file, notStored);

        void Walk(int node)
        {
            if (!done.Add(node) || marks?.IsMarked(DiffArea.Registry, node) == true)
            {
                return;
            }

            var change = tree.ChangeFromOlder(node);
            if (change == ChangeKind.Deleted)
            {
                return;
            }

            if (tree.Kind(node) == DiffItemKind.Value)
            {
                var key = newer.Registry.GetFullPath(tree.NewerIndex(tree.Parent(node)), newer.Names);
                AddValue(key, tree.NewerIndex(node));
                return;
            }

            if (change == ChangeKind.New)
            {
                AddKeyTree(tree.NewerIndex(node));
                return;
            }

            foreach (var child in tree.Children(node))
            {
                Walk(child);
            }
        }

        void AddKeyTree(int key)
        {
            var reg = newer.Registry;
            for (var k = key; k <= key + reg.Descendants[key]; k++)
            {
                var path = reg.GetFullPath(k, newer.Names);
                if (k != key && diff.Ignore.RuleCount > 0 && diff.Ignore.IsIgnoredEntry(IgnoreTarget.Registry, path))
                {
                    k += reg.Descendants[k];
                    continue;
                }

                file.AddKey(path);
                var first = reg.FirstValue[k];
                for (var v = first; v < first + reg.KeyValueCount[k]; v++)
                {
                    AddValue(path, v, checkIgnore: true);
                }
            }
        }

        void AddValue(string keyPath, int value, bool checkIgnore = false)
        {
            var reg = newer.Registry;
            var name = newer.Names.GetString(reg.ValueName[value]);
            if (checkIgnore && diff.Ignore.RuleCount > 0 && diff.Ignore.IsIgnoredEntry(IgnoreTarget.Registry, keyPath + "\\" + name))
            {
                return;
            }

            if (!reg.IsValueDataStored(value))
            {
                notStored.Add(keyPath + "\\" + name);
                return;
            }

            file.AddValue(keyPath, name, reg.ValueDataType[value], reg.GetValueData(value));
        }
    }

    /// <summary>
    /// New and modified files to copy to .\Files (2.6), with the folder structure kept (2.7, D4): files on
    /// the system drive go to "ProgramData\Test\x", files on other drives to "_D\Data\x".
    /// </summary>
    public static List<FileToCopy> Files(DiffResult diff, IEnumerable<int> nodes, ImportanceMarks? marks = null, char? systemDrive = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(nodes);
        var system = char.ToUpperInvariant(systemDrive ?? Path.GetPathRoot(Environment.SystemDirectory)![0]);
        var tree = diff.FileSystem;
        var newer = diff.Newer;
        var fs = newer.FileSystem;
        var result = new List<FileToCopy>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<int>();

        foreach (var node in nodes)
        {
            if (marks?.IsMarkedOrInside(diff, new DiffItem(DiffArea.FileSystem, node)) == true)
            {
                continue;
            }

            Walk(node);
        }

        return result;

        void Walk(int node)
        {
            if (!done.Add(node) || marks?.IsMarked(DiffArea.FileSystem, node) == true)
            {
                return;
            }

            var change = tree.ChangeFromOlder(node);
            if (change == ChangeKind.Deleted)
            {
                return;
            }

            var index = tree.NewerIndex(node);
            if (tree.Kind(node) == DiffItemKind.File)
            {
                if (change is ChangeKind.New or ChangeKind.Modified)
                {
                    Add(index);
                }

                return;
            }

            if (change == ChangeKind.New)
            {
                // A new folder: every file inside it.
                for (var e = index + 1; e <= index + fs.Descendants[index]; e++)
                {
                    if (diff.Ignore.RuleCount > 0 && diff.Ignore.IsIgnoredEntry(IgnoreTarget.FileSystem, fs.GetFullPath(e, newer.Names)))
                    {
                        e += fs.Descendants[e];
                        continue;
                    }

                    if (!fs.IsDirectory(e))
                    {
                        Add(e);
                    }
                }

                return;
            }

            foreach (var child in tree.Children(node))
            {
                Walk(child);
            }
        }

        void Add(int entry)
        {
            var source = fs.GetFullPath(entry, newer.Names);
            if (seen.Add(source))
            {
                result.Add(new FileToCopy(source, TargetPath(source, system), fs.Size[entry]));
            }
        }
    }

    /// <summary>"C:\ProgramData\Test\x" → "ProgramData\Test\x" on the system drive; "D:\Data\x" → "_D\Data\x" elsewhere.</summary>
    public static string TargetPath(string source, char systemDrive)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (source.Length < 3 || source[1] != ':')
        {
            throw new ArgumentException($"Not a drive path: {source}", nameof(source));
        }

        var rest = source[3..];
        return char.ToUpperInvariant(source[0]) == char.ToUpperInvariant(systemDrive)
            ? rest
            : "_" + char.ToUpperInvariant(source[0]) + "\\" + rest;
    }
}
