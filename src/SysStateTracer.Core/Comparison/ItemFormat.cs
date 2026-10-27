using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning.Svc;

namespace SysStateTracer.Core.Comparison;

/// <summary>
/// Text for the snapshot columns of the comparison window and the HTML report (plan §10.4):
/// sizes, times, registry value types and data, service state and start type.
/// </summary>
public static class ItemFormat
{
    public const uint RegNone = 0;
    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;
    public const uint RegBinary = 3;
    public const uint RegDword = 4;
    public const uint RegDwordBigEndian = 5;
    public const uint RegLink = 6;
    public const uint RegMultiSz = 7;
    public const uint RegQword = 11;

    private const int MaxBinaryShown = 64;

    /// <summary>"0 B", "812 KB", "1.4 GB" (1024-based).</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>A FILETIME (UTC) as local time, e.g. "2026-10-27 14:05:12"; empty for 0.</summary>
    public static string Time(long fileTime)
    {
        if (fileTime <= 0)
        {
            return string.Empty;
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    /// <summary>File attributes as letters in Explorer's order: R H S A D … ("RHA").</summary>
    public static string Attributes(uint attributes)
    {
        var text = new StringBuilder();
        Append(0x1, 'R');
        Append(0x2, 'H');
        Append(0x4, 'S');
        Append(0x20, 'A');
        Append(0x800, 'C');
        Append(0x4000, 'E');
        Append(0x400, 'L');
        Append(0x1000, 'O');
        Append(0x200, 'P');
        return text.ToString();

        void Append(uint flag, char letter)
        {
            if ((attributes & flag) != 0)
            {
                text.Append(letter);
            }
        }
    }

    public static string ValueType(uint type) => type switch
    {
        RegNone => "REG_NONE",
        RegSz => "REG_SZ",
        RegExpandSz => "REG_EXPAND_SZ",
        RegBinary => "REG_BINARY",
        RegDword => "REG_DWORD",
        RegDwordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegLink => "REG_LINK",
        RegMultiSz => "REG_MULTI_SZ",
        8 => "REG_RESOURCE_LIST",
        9 => "REG_FULL_RESOURCE_DESCRIPTOR",
        10 => "REG_RESOURCE_REQUIREMENTS_LIST",
        RegQword => "REG_QWORD",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{type:x}"),
    };

    /// <summary>Value data as shown in a column; <paramref name="full"/> = no shortening (tooltips).</summary>
    public static string ValueData(RegTree tree, int value, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!tree.IsValueDataStored(value))
        {
            return string.Create(CultureInfo.InvariantCulture, $"(larger than 1 MB, {tree.ValueDataLength[value]:N0} bytes; not stored)");
        }

        return ValueData(tree.ValueDataType[value], tree.GetValueData(value), full);
    }

    public static string ValueData(uint type, ReadOnlySpan<byte> data, bool full = false)
    {
        switch (type)
        {
            case RegSz or RegExpandSz or RegLink when data.Length % 2 == 0:
                return Utf16(data).TrimEnd('\0');
            case RegMultiSz when data.Length % 2 == 0:
                return string.Join(" ¶ ", Utf16(data).TrimEnd('\0').Split('\0'));
            case RegDword when data.Length == 4:
                var dword = BinaryPrimitives.ReadUInt32LittleEndian(data);
                return string.Create(CultureInfo.InvariantCulture, $"0x{dword:x8} ({dword})");
            case RegDwordBigEndian when data.Length == 4:
                var big = BinaryPrimitives.ReadUInt32BigEndian(data);
                return string.Create(CultureInfo.InvariantCulture, $"0x{big:x8} ({big})");
            case RegQword when data.Length == 8:
                var qword = BinaryPrimitives.ReadUInt64LittleEndian(data);
                return string.Create(CultureInfo.InvariantCulture, $"0x{qword:x16} ({qword})");
            default:
                return Hex(data, full ? int.MaxValue : MaxBinaryShown);
        }
    }

    /// <summary>"01 02 0a …" with at most <paramref name="maxBytes"/> bytes.</summary>
    public static string Hex(ReadOnlySpan<byte> data, int maxBytes = MaxBinaryShown)
    {
        if (data.IsEmpty)
        {
            return "(zero-length binary value)";
        }

        var shown = Math.Min(data.Length, maxBytes);
        var text = new StringBuilder(shown * 3 + 16);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        if (shown < data.Length)
        {
            text.Append(CultureInfo.InvariantCulture, $" … ({data.Length:N0} bytes)");
        }

        return text.ToString();
    }

    public static string ServiceState(uint state) => state switch
    {
        1 => "Stopped",
        2 => "Starting",
        3 => "Stopping",
        4 => "Running",
        5 => "Continuing",
        6 => "Pausing",
        7 => "Paused",
        _ => "Unknown",
    };

    /// <summary>Startup type as services.msc shows it, e.g. "Automatic (Delayed Start)".</summary>
    public static string StartType(uint startType, ServiceFlags flags)
    {
        var name = startType switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            ServicesScanner.UnknownStartType => "Unknown",
            _ => string.Create(CultureInfo.InvariantCulture, $"Start type {startType}"),
        };

        var notes = new List<string>(2);
        if (startType == 2 && flags.HasFlag(ServiceFlags.DelayedAutoStart))
        {
            notes.Add("Delayed Start");
        }

        if (startType is 2 or 3 && flags.HasFlag(ServiceFlags.TriggerStart))
        {
            notes.Add("Trigger Start");
        }

        return notes.Count == 0 ? name : $"{name} ({string.Join(", ", notes)})";
    }

    private static string Utf16(ReadOnlySpan<byte> data) => Encoding.Unicode.GetString(data);
}
