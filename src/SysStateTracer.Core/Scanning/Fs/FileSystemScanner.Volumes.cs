using System.Collections.Concurrent;
using Microsoft.Win32;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Scanning.Fs.Ntfs;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>
/// Profile folders whose contents come from somewhere else without any reparse point. FSLogix Profile Containers
/// attach the profile disk and let a filter driver send everything opened under C:\Users\name to it. The driver
/// hides this from every ordinary call (volume serial number, real path and file ids all look like drive C:),
/// but not from C:'s master file table, which holds only the local, usually empty, folder: a profile folder
/// whose listing shows names its file-table record does not have is virtualized. Such folders (and any that
/// open on another volume) are read like a folder link: listed again in full on every snapshot, through the
/// driver, recorded under their own path, without file ids. The file table is read for this only where FSLogix
/// is installed; a whole-drive scan then reuses it.
/// </summary>
public sealed partial class FileSystemScanner
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>File tables read to find virtualized folders, reused by the whole-drive scan.</summary>
    private readonly ConcurrentDictionary<char, MftIndex> _preloadedTables = new();

    /// <summary>Folders read like links because their contents come from elsewhere (read-only once the scan starts).</summary>
    private HashSet<string> _foreign = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests: used instead of looking at the profile folders when set.</summary>
    internal static IReadOnlyCollection<string>? ForeignFoldersForTests { get; set; }

    /// <summary>
    /// Looks for profile folders inside <paramref name="roots"/> whose contents come from elsewhere; call before
    /// scanning. <paramref name="keepTables"/>: keep the file tables read for this for the whole-drive scan.
    /// </summary>
    private void FindForeignFolders(IReadOnlyList<string> roots, bool keepTables, CancellationToken cancellationToken)
    {
        if (ForeignFoldersForTests is { } forced)
        {
            _foreign = new HashSet<string>(forced.Select(f => f.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            return;
        }

        var profiles = ProfileFolders();
        _foreign = ForeignFolders(roots, profiles);
        if (!FsLogixInstalled() || !ProcessPrivileges.IsElevated)
        {
            return;
        }

        var candidates = InsideRoots(roots, profiles).Where(p => !_foreign.Contains(p)).ToList();
        foreach (var drive in candidates.Select(c => char.ToUpperInvariant(c[0])).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _currentPath, $@"{drive}:\ (file table, looking for FSLogix profiles)");
            var table = MftIndex.TryLoad(drive, null, cancellationToken, out _);
            if (table is null)
            {
                continue;
            }

            foreach (var folder in VirtualizedFolders(table, candidates.Where(c => char.ToUpperInvariant(c[0]) == drive)))
            {
                _foreign.Add(folder);
            }

            if (keepTables)
            {
                _preloadedTables[drive] = table;
            }
        }
    }

    /// <summary>True when folder <paramref name="parent"/>\<paramref name="name"/> is read like a link.</summary>
    private bool IsForeign(string parent, ReadOnlySpan<char> name) =>
        _foreign.Count > 0 && _foreign.Contains(string.Concat(parent, "\\", name));

    /// <summary>
    /// The <paramref name="candidates"/> strictly inside one of <paramref name="roots"/> that open on another volume
    /// than their drive. Folders with a reparse point are left out: they are handled as links.
    /// </summary>
    internal static HashSet<string> ForeignFolders(IEnumerable<string> roots, IEnumerable<string> candidates)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var driveSerials = new Dictionary<char, uint?>();
        foreach (var path in InsideRoots(roots, candidates))
        {
            try
            {
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var drive = char.ToUpperInvariant(path[0]);
            if (!driveSerials.TryGetValue(drive, out var driveSerial))
            {
                driveSerial = VolumeSerialOf(drive + @":\");
                driveSerials[drive] = driveSerial;
            }

            if (driveSerial is { } expected && VolumeSerialOf(path) is { } actual && actual != expected)
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>
    /// The <paramref name="candidates"/> whose listing shows a name that their record in <paramref name="table"/> does
    /// not have (or that have no record): their contents come from elsewhere, through a filter driver.
    /// </summary>
    internal static List<string> VirtualizedFolders(MftIndex table, IEnumerable<string> candidates)
    {
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            var path = candidate.TrimEnd('\\');
            List<string> listed;
            try
            {
                // Links ("All Users", "Default User") show their target's names: they are handled as links.
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                listed = [.. Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
                    .Select(Path.GetFileName)
                    .OfType<string>()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var record = path.Length > 3 ? table.Find(path.AsSpan(3)) : MftIndex.RootRecord;
            var inTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (record >= 0)
            {
                foreach (var link in table.Children(record))
                {
                    inTable.Add(table.Name(link).ToString());
                }
            }

            if (HasNamesNotIn(listed, inTable))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="listed"/> has a name missing from <paramref name="inTable"/>.</summary>
    internal static bool HasNamesNotIn(IEnumerable<string> listed, IReadOnlySet<string> inTable) =>
        listed.Any(name => !inTable.Contains(name));

    /// <summary>True when <paramref name="folder"/> (not a drive root) opens on another volume than its drive.</summary>
    internal static bool IsOnOtherVolume(string folder)
    {
        var path = folder.TrimEnd('\\');
        if (path.Length < 4 || path[1] != ':')
        {
            return false;
        }

        return VolumeSerialOf(path[..2] + "\\") is { } drive && VolumeSerialOf(path) is { } actual && actual != drive;
    }

    /// <summary>Serial number of the volume <paramref name="folder"/> opens on (links followed), or null.</summary>
    internal static uint? VolumeSerialOf(string folder)
    {
        using var handle = NativeMethods.CreateFile(
            OpenPath(folder.Length > 3 ? folder.TrimEnd('\\') : folder),
            NativeMethods.FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            0,
            FileMode.Open,
            NativeMethods.FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            return null;
        }

        return NativeMethods.GetFileInformationByHandle(handle, out var info) ? info.VolumeSerialNumber : null;
    }

    /// <summary>The <paramref name="candidates"/> (drive paths) strictly inside one of <paramref name="roots"/>.</summary>
    private static List<string> InsideRoots(IEnumerable<string> roots, IEnumerable<string> candidates)
    {
        var rootList = roots.ToList();
        return [.. candidates
            .Select(c => c.TrimEnd('\\'))
            .Where(p => p.Length >= 4 && p[1] == ':' &&
                rootList.Any(r => IsSameOrInside(p, r) && !string.Equals(p, r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>FSLogix (Profile or Office Containers) is installed: its filter driver or service is registered.</summary>
    private static bool FsLogixInstalled()
    {
        try
        {
            using var driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\frxdrv");
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\frxsvc");
            return driver is not null || service is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Every user profile folder Windows knows of (ProfileImagePath under ProfileList), plus the folders of the profiles directory.</summary>
    private static List<string> ProfileFolders()
    {
        var result = new List<string>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
            if (list is null)
            {
                return result;
            }

            foreach (var sid in list.GetSubKeyNames())
            {
                using var profile = list.OpenSubKey(sid);
                if (profile?.GetValue("ProfileImagePath") is string image && image.Length > 0)
                {
                    result.Add(Environment.ExpandEnvironmentVariables(image));
                }
            }

            if (list.GetValue("ProfilesDirectory") is string directory && directory.Length > 0)
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory);
                if (Directory.Exists(expanded))
                {
                    result.AddRange(Directory.EnumerateDirectories(expanded));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Profiles that cannot be listed are scanned like any folder.
        }

        return result;
    }
}
