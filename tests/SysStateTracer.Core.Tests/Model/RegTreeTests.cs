using System.Text;
using SysStateTracer.Core.Model;
using static SysStateTracer.Core.Tests.SampleSnapshots;

namespace SysStateTracer.Core.Tests.Model;

public class RegTreeTests
{
    [Fact]
    public void Build_SortsKeysAndValues()
    {
        var snapshot = SampleSnapshots.Create();
        var reg = snapshot.Registry;
        var names = snapshot.Names;

        var keys = Enumerable.Range(0, reg.KeyCount).Select(k => reg.GetFullPath(k, names)).ToArray();
        Assert.Equal(
            new[]
            {
                "HKLM",
                @"HKLM\SOFTWARE",
                @"HKLM\SOFTWARE\7-Zip",
                @"HKLM\SOFTWARE\Classes",
                "HKU",
                @"HKU\S-1-5-21-1000",
                @"HKU\S-1-5-21-1000\Environment",
            },
            keys);

        var app = Array.IndexOf(keys, @"HKLM\SOFTWARE\7-Zip");
        var first = reg.FirstValue[app];
        var valueNames = Enumerable.Range(first, reg.KeyValueCount[app]).Select(v => names.GetString(reg.ValueName[v])).ToArray();
        Assert.Equal(new[] { string.Empty, "Big", "Path", "Path64", "Version" }, valueNames);
    }

    [Fact]
    public void Build_StoresIdenticalDataOnce()
    {
        var snapshot = SampleSnapshots.Create();
        var reg = snapshot.Registry;
        var path = FindValue(snapshot, @"HKLM\SOFTWARE\7-Zip", "Path");
        var path64 = FindValue(snapshot, @"HKLM\SOFTWARE\7-Zip", "Path64");

        Assert.Equal(reg.ValueBlob[path], reg.ValueBlob[path64]);
        Assert.Equal(@"C:\Program Files\7-Zip" + "\0", Encoding.Unicode.GetString(reg.GetValueData(path)));
        Assert.Equal(1u, reg.ValueDataType[path]);
    }

    [Fact]
    public void Build_KeepsDataAcrossStorageChunks()
    {
        // 12 distinct values of 700 KB (several 4 MB chunks) plus repeats of each.
        var names = new NameTable();
        var builder = new RegTreeBuilder(names);
        var root = builder.AddRoot("HKLM", 0);
        var data = Enumerable.Range(0, 12).Select(i =>
        {
            var bytes = new byte[700 * 1024 + i];
            new Random(i).NextBytes(bytes);
            return bytes;
        }).ToArray();

        for (var i = 0; i < data.Length; i++)
        {
            builder.AddValue(root, $"v{i:D2}", 3, data[i]);
            builder.AddValue(root, $"w{i:D2}", 3, data[i]);
        }

        var reg = builder.Build();

        Assert.Equal(data.Length, reg.Blobs.Count);
        Assert.Equal(data.Sum(d => (long)d.Length), reg.Blobs.Data.Length);
        for (var v = 0; v < reg.ValueCount; v++)
        {
            var i = int.Parse(names.GetString(reg.ValueName[v]).AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(data[i].AsSpan().SequenceEqual(reg.GetValueData(v)), $"value {v}");
        }
    }

    [Fact]
    public void Build_KeepsOnlyHashOfOversizedValues()
    {
        var snapshot = SampleSnapshots.Create();
        var reg = snapshot.Registry;
        var big = FindValue(snapshot, @"HKLM\SOFTWARE\7-Zip", "Big");

        Assert.False(reg.IsValueDataStored(big));
        Assert.True(reg.GetValueData(big).IsEmpty);
        Assert.Equal(RegTreeBuilder.MaxStoredValueSize + 1, reg.ValueDataLength[big]);
        Assert.NotEqual(0UL, reg.ValueDataHash[big]);
    }

    [Fact]
    public void Digest_IgnoresKeyTimestampButSeesValueChanges()
    {
        static RegTree Build(long keyTime, int version)
        {
            var b = new RegTreeBuilder(new NameTable());
            var root = b.AddRoot("HKLM", Time(1));
            var key = b.AddKey(root, "App", keyTime);
            b.AddValue(key, "Version", 4, BitConverter.GetBytes(version));
            return b.Build();
        }

        var baseline = Build(Time(1), 1);
        var touched = Build(Time(9), 1);
        var changed = Build(Time(1), 2);

        Assert.Equal(baseline.Digest.ToArray(), touched.Digest.ToArray());
        Assert.NotEqual(baseline.Digest[1], changed.Digest[1]);
        Assert.NotEqual(baseline.Digest[0], changed.Digest[0]);
    }

    [Fact]
    public void Services_AreSortedByName()
    {
        var snapshot = SampleSnapshots.Create();
        var services = snapshot.Services;

        var names = Enumerable.Range(0, services.Count).Select(i => snapshot.Names.GetString(services.Name[i])).ToArray();

        Assert.Equal(new[] { "BITS", "edgeupdate", "Spooler" }, names);
        Assert.Equal(ServiceFlags.DelayedAutoStart | ServiceFlags.TriggerStart, services.Flags[0]);
        Assert.Equal("Print Spooler", snapshot.Names.GetString(services.DisplayName[2]));
    }

    internal static int FindValue(Snapshot snapshot, string keyPath, string valueName)
    {
        var reg = snapshot.Registry;
        for (var k = 0; k < reg.KeyCount; k++)
        {
            if (reg.GetFullPath(k, snapshot.Names) != keyPath)
            {
                continue;
            }

            for (var v = reg.FirstValue[k]; v < reg.FirstValue[k] + reg.KeyValueCount[k]; v++)
            {
                if (snapshot.Names.GetString(reg.ValueName[v]) == valueName)
                {
                    return v;
                }
            }
        }

        throw new InvalidOperationException($"Value {keyPath}\\{valueName} not found.");
    }
}
