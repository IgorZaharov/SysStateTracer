using SysStateTracer.Core.Config;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Snapshots;

/// <summary>A .stsnap file that could not be read (damaged, newer format, locked), with the reason.</summary>
public sealed record UnreadableSnapshot(string Path, string Reason);

/// <summary>
/// The snapshots in a folder (W1 list, command line). "Newest" is the creation time stored in the snapshot,
/// not the file time, which changes on copy (plan 2.13). A snapshot is found by its name: the file name
/// without .stsnap (plan 2.10).
/// </summary>
public sealed class SnapshotCatalog
{
    public SnapshotCatalog(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
    }

    public string Folder { get; }

    /// <summary>Readable snapshots, newest first; unreadable files are reported in <paramref name="unreadable"/>.</summary>
    public List<SnapshotInfo> List(List<UnreadableSnapshot>? unreadable = null)
    {
        var result = new List<SnapshotInfo>();
        if (!Directory.Exists(Folder))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(Folder, "*" + SnapshotNaming.FileExtension))
        {
            if (!path.EndsWith(SnapshotNaming.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Windows pattern matching can also return longer extensions
            }

            try
            {
                result.Add(SnapshotReader.ReadInfo(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
            {
                Log.Warning($"Snapshot cannot be read: {path}", ex);
                unreadable?.Add(new UnreadableSnapshot(path, ex.Message));
            }
        }

        result.Sort((a, b) => b.Meta.StartedAt.CompareTo(a.Meta.StartedAt));
        return result;
    }

    /// <summary>The newest <paramref name="count"/> snapshots, newest first.</summary>
    public List<SnapshotInfo> Newest(int count) => [.. List().Take(count)];

    /// <summary>
    /// Path of the snapshot called <paramref name="name"/> (".stsnap" may be included), or of a .stsnap file
    /// given by path; null when there is none.
    /// </summary>
    public string? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            var full = Path.GetFullPath(name);
            return File.Exists(full) ? full : null;
        }

        if (name.EndsWith(SnapshotNaming.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^SnapshotNaming.FileExtension.Length];
        }

        if (!SnapshotNaming.IsValidName(name))
        {
            return null;
        }

        var path = Path.Combine(Folder, name + SnapshotNaming.FileExtension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Path a snapshot named <paramref name="name"/> has in this folder.</summary>
    public string PathFor(string name) => Path.Combine(Folder, name + SnapshotNaming.FileExtension);

    /// <summary>Renames the file; throws <see cref="IOException"/> when the new name is taken.</summary>
    public static string Rename(string path, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!SnapshotNaming.IsValidName(newName))
        {
            throw new ArgumentException($"'{newName}' cannot be used as a snapshot name.", nameof(newName));
        }

        var target = Path.Combine(Path.GetDirectoryName(path)!, newName + SnapshotNaming.FileExtension);
        var caseOnly = string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(target))
        {
            throw new IOException($"A snapshot named '{newName}' already exists.");
        }

        File.Move(path, target);
        return target;
    }
}
