using System.Buffers.Binary;
using System.Diagnostics;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using SysStateTracer.Core.Tests.Model;

namespace SysStateTracer.Core.Tests.Storage;

public class SnapshotStorageTests
{
    [Fact]
    public void WriteThenLoad_RoundTripsEveryPart()
    {
        using var dir = new TempDirectory();
        var path = dir.File("round-trip.stsnap");
        var original = SampleSnapshots.Create();

        SnapshotWriter.Write(path, original);
        var loaded = SnapshotReader.Load(path);

        AssertSameNames(original.Names, loaded.Names);

        var a = original.FileSystem;
        var b = loaded.FileSystem;
        Assert.Equal(a.Parent.ToArray(), b.Parent.ToArray());
        Assert.Equal(a.Name.ToArray(), b.Name.ToArray());
        Assert.Equal(a.Attributes.ToArray(), b.Attributes.ToArray());
        Assert.Equal(a.Size.ToArray(), b.Size.ToArray());
        Assert.Equal(a.LastWriteTime.ToArray(), b.LastWriteTime.ToArray());
        Assert.Equal(a.Descendants.ToArray(), b.Descendants.ToArray());
        Assert.Equal(a.ChildFolders.ToArray(), b.ChildFolders.ToArray());
        Assert.Equal(a.ChildFiles.ToArray(), b.ChildFiles.ToArray());
        Assert.Equal(a.Digest.ToArray(), b.Digest.ToArray());

        var r = original.Registry;
        var s = loaded.Registry;
        Assert.Equal(r.Parent.ToArray(), s.Parent.ToArray());
        Assert.Equal(r.Name.ToArray(), s.Name.ToArray());
        Assert.Equal(r.LastWriteTime.ToArray(), s.LastWriteTime.ToArray());
        Assert.Equal(r.FirstValue.ToArray(), s.FirstValue.ToArray());
        Assert.Equal(r.KeyValueCount.ToArray(), s.KeyValueCount.ToArray());
        Assert.Equal(r.Descendants.ToArray(), s.Descendants.ToArray());
        Assert.Equal(r.Digest.ToArray(), s.Digest.ToArray());
        Assert.Equal(r.ValueName.ToArray(), s.ValueName.ToArray());
        Assert.Equal(r.ValueDataType.ToArray(), s.ValueDataType.ToArray());
        Assert.Equal(r.ValueDataHash.ToArray(), s.ValueDataHash.ToArray());
        Assert.Equal(r.ValueBlob.ToArray(), s.ValueBlob.ToArray());
        Assert.Equal(r.ValueDataLength.ToArray(), s.ValueDataLength.ToArray());
        for (var v = 0; v < r.ValueCount; v++)
        {
            Assert.Equal(r.GetValueData(v).ToArray(), s.GetValueData(v).ToArray());
        }

        var x = original.Services;
        var y = loaded.Services;
        Assert.Equal(x.Name.ToArray(), y.Name.ToArray());
        Assert.Equal(x.DisplayName.ToArray(), y.DisplayName.ToArray());
        Assert.Equal(x.ServiceType.ToArray(), y.ServiceType.ToArray());
        Assert.Equal(x.State.ToArray(), y.State.ToArray());
        Assert.Equal(x.StartType.ToArray(), y.StartType.ToArray());
        Assert.Equal(x.Flags.ToArray(), y.Flags.ToArray());

        Assert.Equal(original.Skipped, loaded.Skipped);
        Assert.Equal(SnapshotParts.All, loaded.LoadedParts);
    }

    [Fact]
    public void Write_FillsCountsAndKeepsMeta()
    {
        using var dir = new TempDirectory();
        var path = dir.File("meta.stsnap");
        var original = SampleSnapshots.Create();

        SnapshotWriter.Write(path, original);
        var info = SnapshotReader.ReadInfo(path);
        var meta = info.Meta;

        Assert.Equal("meta", info.Name);
        Assert.Equal(new FileInfo(path).Length, info.FileLength);
        Assert.Equal(1, info.FormatMajor);
        Assert.Equal("20261027_14-05-12_WS-0142", meta.Name);
        Assert.Equal(original.Meta.StartedAt, meta.StartedAt);
        Assert.Equal(original.Meta.CompletedAt, meta.CompletedAt);
        Assert.Equal("WS-0142", meta.ComputerName);
        Assert.Equal("Windows 11 Pro 25H2", meta.OsEdition);
        Assert.Equal(SnapshotMode.Incremental, meta.Mode);
        Assert.Equal("D: has no change journal", meta.ModeReason);
        Assert.Equal(original.Meta.Scope, meta.Scope);
        Assert.Equal(12, meta.IgnoreRuleCount);
        Assert.Equal(5300, meta.Timings.RegistryMs);
        Assert.True(meta.Timings.WriteMs >= 0);
    }

    [Fact]
    public void Write_CountsMatchTheData()
    {
        using var dir = new TempDirectory();
        var path = dir.File("counts.stsnap");
        var original = SampleSnapshots.Create();

        SnapshotWriter.Write(path, original);
        var counts = SnapshotReader.ReadInfo(path).Meta.Counts;

        Assert.Equal(8, counts.Folders);        // C:\ apps boot ProgramData Windows System32 D:\ Data
        Assert.Equal(3, counts.Files);          // pagefile.sys notepad.exe kernel32.dll
        Assert.Equal(7, counts.RegistryKeys);
        Assert.Equal(6, counts.RegistryValues);
        Assert.Equal(3, counts.Services);
        Assert.Equal(2, counts.Skipped);
    }

    [Fact]
    public void Load_OnlyRequestedParts()
    {
        using var dir = new TempDirectory();
        var path = dir.File("parts.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        var fsOnly = SnapshotReader.Load(path, SnapshotParts.FileSystem);
        Assert.Equal(11, fsOnly.FileSystem.Count);
        Assert.Equal(0, fsOnly.Registry.KeyCount);
        Assert.Equal(0, fsOnly.Services.Count);
        Assert.Empty(fsOnly.Skipped);
        Assert.Equal(SnapshotParts.FileSystem, fsOnly.LoadedParts);

        var registryWithoutData = SnapshotReader.Load(path, SnapshotParts.Registry);
        var loaded = registryWithoutData.Registry;
        var pathValue = RegTreeTests.FindValue(registryWithoutData, @"HKLM\SOFTWARE\7-Zip", "Path");
        Assert.Equal(7, loaded.KeyCount);
        Assert.True(loaded.IsValueDataStored(pathValue));
        Assert.True(loaded.GetValueData(pathValue).IsEmpty);
        Assert.NotEqual(0UL, loaded.ValueDataHash[pathValue]);
    }

    [Fact]
    public void Write_EmptySnapshot_RoundTrips()
    {
        using var dir = new TempDirectory();
        var path = dir.File("empty.stsnap");
        var names = new NameTable();
        names.Freeze();

        SnapshotWriter.Write(path, new Snapshot(new SnapshotMeta { Name = "empty" }, names));
        var loaded = SnapshotReader.Load(path);

        Assert.Equal(0, loaded.Names.Count);
        Assert.Equal(0, loaded.FileSystem.Count);
        Assert.Equal(0, loaded.Registry.KeyCount);
        Assert.Equal(0, loaded.Services.Count);
    }

    [Fact]
    public void Write_LeavesNoTempFile_AndRefusesToOverwrite()
    {
        using var dir = new TempDirectory();
        var path = dir.File("once.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Throws<IOException>(() => SnapshotWriter.Write(path, SampleSnapshots.Create()));

        SnapshotWriter.Write(path, SampleSnapshots.Create(), new SnapshotWriteOptions { Overwrite = true });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_Cancelled_LeavesNothingBehind()
    {
        using var dir = new TempDirectory();
        var path = dir.File("cancelled.stsnap");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SnapshotWriter.Write(path, SampleSnapshots.Create(), cancellationToken: cts.Token));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_DamagedBlock_IsDetected()
    {
        using var dir = new TempDirectory();
        var path = dir.File("damaged.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        FlipByte(path, 520); // inside the first data block

        Assert.NotNull(SnapshotReader.ReadInfo(path)); // header and meta are still fine
        var ex = Assert.Throws<SnapshotFormatException>(() => SnapshotReader.Load(path));
        Assert.Contains("damaged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadInfo_TruncatedFile_IsDetected()
    {
        using var dir = new TempDirectory();
        var path = dir.File("truncated.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        using (var stream = File.OpenWrite(path))
        {
            stream.SetLength(stream.Length - 10);
        }

        var ex = Assert.Throws<SnapshotFormatException>(() => SnapshotReader.ReadInfo(path));
        Assert.Contains("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadInfo_NotASnapshot_IsDetected()
    {
        using var dir = new TempDirectory();
        var path = dir.File("text.stsnap");
        File.WriteAllText(path, new string('x', 2000));

        var ex = Assert.Throws<SnapshotFormatException>(() => SnapshotReader.ReadInfo(path));
        Assert.Contains("not a SysStateTracer snapshot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadInfo_DamagedHeader_IsDetected()
    {
        using var dir = new TempDirectory();
        var path = dir.File("header.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        FlipByte(path, SnapshotFormat.OffCreatedTicks);

        Assert.Throws<SnapshotFormatException>(() => SnapshotReader.ReadInfo(path));
    }

    [Fact]
    public void ReadInfo_NewerMajorVersion_AsksToUpdate()
    {
        using var dir = new TempDirectory();
        var path = dir.File("future.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());

        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SnapshotFormat.OffMajor), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(SnapshotFormat.OffHeaderHash),
            SnapshotFormat.Hash(bytes.AsSpan(0, SnapshotFormat.OffHeaderHash)));
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<SnapshotFormatException>(() => SnapshotReader.ReadInfo(path));
        Assert.Contains("newer SysStateTracer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadInfo_IsFast()
    {
        using var dir = new TempDirectory();
        var path = dir.File("fast.stsnap");
        SnapshotWriter.Write(path, SampleSnapshots.Create());
        SnapshotReader.ReadInfo(path); // warm-up (JIT)

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            SnapshotReader.ReadInfo(path);
        }

        // Target is < 5 ms per file (plan Step 12); generous bound to keep CI stable.
        Assert.True(watch.Elapsed.TotalMilliseconds / 20 < 25, $"{watch.Elapsed.TotalMilliseconds / 20:F2} ms per ReadInfo");
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        var value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    private static void AssertSameNames(NameTable expected, NameTable actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected.GetString(i), actual.GetString(i));
        }
    }
}
