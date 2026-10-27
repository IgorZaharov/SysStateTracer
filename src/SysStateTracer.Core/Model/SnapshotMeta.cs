using System.Text.Json.Serialization;

namespace SysStateTracer.Core.Model;

public enum SnapshotMode
{
    /// <summary>Everything was read from the system.</summary>
    Full,

    /// <summary>Unchanged parts were reused from SysStateTracer.cache (plan §8.5).</summary>
    Incremental,
}

/// <summary>Descriptive data stored with a snapshot and shown in Snapshot Properties (W2).</summary>
public sealed class SnapshotMeta
{
    /// <summary>Name at creation time; the current name is the file name (plan 2.10).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public string ComputerName { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    /// <summary>e.g. "Windows 11 Pro 25H2".</summary>
    public string OsEdition { get; set; } = string.Empty;

    /// <summary>e.g. "10.0.26200.6899".</summary>
    public string OsVersion { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public SnapshotMode Mode { get; set; } = SnapshotMode.Full;

    /// <summary>Why parts had to be scanned in full, when <see cref="Mode"/> is Incremental or a fallback happened.</summary>
    public string? ModeReason { get; set; }

    /// <summary>Roots included, e.g. "C:\", "HKLM\SOFTWARE", "Services".</summary>
    public List<string> Scope { get; set; } = [];

    /// <summary>How each file-system root was read, e.g. "C:\: master file table".</summary>
    public List<string> FileSystemMethods { get; set; } = [];

    /// <summary>Number of Global Ignore rules applied while scanning.</summary>
    public int IgnoreRuleCount { get; set; }

    public SnapshotTimings Timings { get; set; } = new();

    /// <summary>Filled in by the writer from the stored data.</summary>
    public SnapshotCounts Counts { get; set; } = new();
}

/// <summary>Durations in milliseconds.</summary>
public sealed class SnapshotTimings
{
    public long FileSystemMs { get; set; }

    public long RegistryMs { get; set; }

    public long ServicesMs { get; set; }

    public long WriteMs { get; set; }

    public long TotalMs { get; set; }
}

public sealed class SnapshotCounts
{
    public long Folders { get; set; }

    public long Files { get; set; }

    public long RegistryKeys { get; set; }

    public long RegistryValues { get; set; }

    public long Services { get; set; }

    public long Skipped { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SnapshotMeta))]
internal sealed partial class SnapshotMetaJsonContext : JsonSerializerContext
{
}
