using System.Text.Json;
using System.Text.Json.Serialization;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;

namespace SysStateTracer.Core.Scanning;

/// <summary>What SysStateTracer.cache was built from (plan §7.8); stored inside the cache file.</summary>
internal sealed class CacheInfo
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string ComputerName { get; set; } = string.Empty;

    public List<string> FileSystemRoots { get; set; } = [];

    public List<string> RegistryRoots { get; set; } = [];

    public string IgnoreFingerprint { get; set; } = string.Empty;

    /// <summary>Who took it, and with which rights: what could be read depends on both (F9).</summary>
    public string UserName { get; set; } = string.Empty;

    public bool Elevated { get; set; }

    /// <summary>Change-journal checkpoints, taken before the scan started.</summary>
    public List<CacheVolume> Volumes { get; set; } = [];
}

internal sealed class CacheVolume
{
    /// <summary>Drive letter, e.g. "C".</summary>
    public string Drive { get; set; } = string.Empty;

    public uint SerialNumber { get; set; }

    public ulong JournalId { get; set; }

    public long CheckpointUsn { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CacheInfo))]
internal sealed partial class CacheInfoJsonContext : JsonSerializerContext
{
}

/// <summary>
/// SysStateTracer.cache: the last snapshot taken on this computer, with file reference numbers and the
/// change-journal checkpoints, so the next snapshot can re-read only what changed (plan §7.8, §8.5).
/// It never affects correctness: when it is missing, stale or unreadable, the next snapshot is a full one.
/// </summary>
internal static class SnapshotCache
{
    public static void Save(string path, Snapshot snapshot, CacheInfo info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(info, CacheInfoJsonContext.Default.CacheInfo);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Writing fills in timings and counts of the snapshot's meta; keep the real snapshot's values.
        var writeMs = snapshot.Meta.Timings.WriteMs;
        try
        {
            SnapshotWriter.Write(path, snapshot, new SnapshotWriteOptions { Overwrite = true, CompressionQuality = 1, CacheInfo = json });
        }
        finally
        {
            snapshot.Meta.Timings.WriteMs = writeMs;
        }
    }

    /// <summary>The cached snapshot (all parts, with value data) and its description; null with a reason otherwise.</summary>
    public static (Snapshot Snapshot, CacheInfo Info)? TryLoad(string path, CancellationToken cancellationToken, out string reason)
    {
        if (!File.Exists(path))
        {
            reason = "no previous snapshot on this computer";
            return null;
        }

        try
        {
            using var reader = SnapshotReader.Open(path);
            var json = reader.ReadColumn<byte>(SectionKind.CacheInfo, cancellationToken);
            if (json.Length == 0)
            {
                reason = "the cache file has no description";
                return null;
            }

            var info = JsonSerializer.Deserialize(json, CacheInfoJsonContext.Default.CacheInfo);
            if (info is null || info.FormatVersion != CacheInfo.CurrentFormatVersion)
            {
                reason = "the cache file is from another version";
                return null;
            }

            var snapshot = reader.Load(SnapshotParts.All, cancellationToken);
            reason = string.Empty;
            return (snapshot, info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException or JsonException)
        {
            reason = $"the cache file could not be read ({ex.Message})";
            return null;
        }
    }
}
