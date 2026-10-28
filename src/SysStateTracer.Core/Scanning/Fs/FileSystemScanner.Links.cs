using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;

namespace SysStateTracer.Core.Scanning.Fs;

/// <summary>
/// Folder links (GitHub issue #2): profile folders such as Desktop or Documents may be junctions or symbolic
/// links to a network share or another drive. Their contents are read through the link and recorded under
/// the local path (C:\Users\Alex\Desktop\…), so changes there show up like any other. A link is followed only
/// when its target is outside every scanned folder and does not overlap a target already followed, which
/// keeps the classic profile junctions ("Application Data", "Documents and Settings") from being read twice
/// and makes loops impossible. A folder with a volume mounted on it (e.g. a profile disk mounted at
/// C:\Users\Alex by FSLogix Profile Containers) is read through that volume: asking where such a folder leads
/// gives the folder itself, which would look like "inside the scanned folders" and be skipped. Reparse folders
/// that are not links (e.g. cloud-file placeholders available on this PC) are read in place. Everything read
/// through a link is listed again on every snapshot, also an incremental one, because the change journal of
/// this drive does not see changes on the other side.
/// </summary>
public sealed partial class FileSystemScanner
{
    /// <summary>A folder with a reparse point that can be read (not an online-only cloud folder).</summary>
    private static bool IsLink(uint attributes) =>
        (attributes & NativeMethods.FileAttributeReparsePoint) != 0 && (attributes & NativeMethods.FileAttributeRecallOnOpen) == 0;

    /// <summary>The path to open: "C:" → \\?\C:\, "C:\X" → \\?\C:\X, \\server\share → \\?\UNC\server\share.</summary>
    internal static string OpenPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            // A volume mounted in a folder: its root needs the trailing '\'.
            return path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.EndsWith('}') ? path + "\\" : path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + (path.Length == 2 ? path + "\\" : path);
    }

    /// <summary>"\\?\UNC\server\share\x" → "\\server\share\x", "\\?\C:\x" → "C:\x"; no trailing '\'.</summary>
    internal static string NormalizeTarget(string target)
    {
        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            target = @"\\" + target[8..];
        }
        else if (target.StartsWith(@"\\?\", StringComparison.Ordinal) && target.Length >= 6 && target[5] == ':')
        {
            target = target[4..];
        }

        return target.Length > 3 ? target.TrimEnd('\\') : target;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    internal static bool IsSameOrInside(string path, string folder)
    {
        var parent = TrimForJoin(folder.Length > 3 ? folder.TrimEnd('\\') : folder);
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TrimForJoin(path), parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads what lies behind the folder links found so far, in rounds: links found behind a link are
    /// handled in the next round, with the same rules.
    /// </summary>
    private void FollowLinks(FileSystemScanOptions options, CancellationToken cancellationToken)
    {
        var roots = NormalizeRoots(options.Roots);
        var followed = new List<string>();

        // Volumes already scanned as a whole drive (C:\ etc.): mounted again in a folder, they are not read twice.
        var rootVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(r => r.Length <= 3))
        {
            if (NativeMethods.GetMountedVolume(root[..2]) is { } volume)
            {
                rootVolumes.Add(NormalizeTarget(volume));
            }
        }

        while (!_links.IsEmpty)
        {
            var jobs = new List<DirectoryJob>();
            while (_links.TryDequeue(out var link))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Ignore.IsIgnored(IgnoreTarget.FileSystem, link.Path))
                {
                    continue;
                }

                // A profile folder on another volume without a reparse point (FSLogix profile disk): read in place.
                if (_foreign.Contains(link.Path))
                {
                    _methods.Enqueue($"{link.Path}: on another volume (e.g. an FSLogix profile disk), read there");
                    jobs.Add(new DirectoryJob(link.Index, link.Path, OpenPath(link.Path)));
                    continue;
                }

                // A mounted volume (a VHD or a second disk mounted in a folder).
                if (NativeMethods.GetMountedVolume(link.Path) is { } mounted)
                {
                    var volume = NormalizeTarget(mounted);
                    if (rootVolumes.Contains(volume) || followed.Contains(volume, StringComparer.OrdinalIgnoreCase))
                    {
                        continue; // read already, as a drive or at another mount point
                    }

                    followed.Add(volume);
                    _methods.Enqueue($"{link.Path}: mounted volume {volume}, read there");
                    jobs.Add(new DirectoryJob(link.Index, link.Path, volume));
                    continue;
                }

                string? target;
                try
                {
                    target = Directory.ResolveLinkTarget(OpenPath(link.Path), returnFinalTarget: true)?.FullName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _skipped.Add(link.Path, ex is UnauthorizedAccessException ? Win32Errors.AccessDenied : ex.HResult & 0xFFFF);
                    continue;
                }

                if (target is null)
                {
                    // Not a link (e.g. a cloud-file folder on this PC): its contents are here, read them in place.
                    jobs.Add(new DirectoryJob(link.Index, link.Path, OpenPath(link.Path)));
                    continue;
                }

                target = NormalizeTarget(target);
                if (target.Length > 2 && target[1] == ':')
                {
                    target = NativeMethods.GetLongPath(target); // compare real names, not 8.3 short names
                }
                if (roots.Any(r => IsSameOrInside(target, r)) ||
                    followed.Any(f => IsSameOrInside(target, f) || IsSameOrInside(f, target)))
                {
                    continue; // read already, where it really is
                }

                followed.Add(target);
                _methods.Enqueue($"{link.Path}: link to {target}, read there");
                jobs.Add(new DirectoryJob(link.Index, link.Path, target));
            }

            ListFolders(jobs, options, cancellationToken);
        }
    }
}
