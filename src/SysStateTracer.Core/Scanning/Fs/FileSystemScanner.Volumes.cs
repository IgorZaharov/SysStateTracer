using Microsoft.Win32;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>
/// Profile folders whose contents are on another volume without any reparse point. FSLogix Profile Containers
/// attach the profile disk and let a filter driver send everything opened under C:\Users\name to it: the folder
/// looks ordinary, but drive C:'s change journal never sees what changes inside, and the master file table of
/// C: only holds the empty local folder. Such a folder is found by opening it and comparing the volume serial
/// number with its drive's, and is then read like a folder link: listed again in full on every snapshot,
/// recorded under its own path, without file ids.
/// </summary>
public sealed partial class FileSystemScanner
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Folders on another volume found for this scan (read-only once the scan starts).</summary>
    private HashSet<string> _foreign = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests: used instead of looking at the profile folders when set.</summary>
    internal static IReadOnlyCollection<string>? ForeignFoldersForTests { get; set; }

    /// <summary>Looks for profile folders on another volume inside <paramref name="roots"/>; call before scanning.</summary>
    private void FindForeignFolders(IReadOnlyList<string> roots)
    {
        _foreign = ForeignFoldersForTests is { } forced
            ? new HashSet<string>(forced.Select(f => f.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)
            : ForeignFolders(roots, ProfileFolders());
    }

    /// <summary>True when folder <paramref name="parent"/>\<paramref name="name"/> is on another volume.</summary>
    private bool IsForeign(string parent, ReadOnlySpan<char> name) =>
        _foreign.Count > 0 && _foreign.Contains(string.Concat(parent, "\\", name));

    /// <summary>
    /// The <paramref name="candidates"/> strictly inside one of <paramref name="roots"/> that open on another volume
    /// than their drive. Folders with a reparse point are left out: they are handled as links.
    /// </summary>
    internal static HashSet<string> ForeignFolders(IEnumerable<string> roots, IEnumerable<string> candidates)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootList = roots.ToList();
        var driveSerials = new Dictionary<char, uint?>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = candidate.TrimEnd('\\');
            if (path.Length < 4 || path[1] != ':' ||
                !rootList.Any(r => IsSameOrInside(path, r) && !string.Equals(path, r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

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

    /// <summary>Serial number of the volume <paramref name="folder"/> really opens on (filters and links followed), or null.</summary>
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
