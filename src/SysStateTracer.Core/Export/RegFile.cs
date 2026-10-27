using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SysStateTracer.Core.Comparison;

namespace SysStateTracer.Core.Export;

/// <summary>
/// A .reg file exactly as Regedit writes it (plan §10.8): "Windows Registry Editor Version 5.00",
/// UTF-16 LE with BOM, <c>"name"="text"</c>, <c>dword:</c>, <c>hex(b):</c>, <c>hex(2):</c>, <c>hex(7):</c>,
/// <c>hex:</c>, <c>hex(N):</c>, <c>@</c> for the default value, <c>=-</c> to delete a value.
/// Strings are written as text only when that gives back the same bytes on import; otherwise as hex,
/// so an export, import and re-scan round trip is byte-identical.
/// </summary>
public sealed class RegFile
{
    public const string Header = "Windows Registry Editor Version 5.00";

    private const int LineWidth = 80;

    // Key path → its value lines, in the order keys were first added.
    private readonly List<(string Key, List<string> Lines, HashSet<string> Names)> _keys = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public int KeyCount => _keys.Count;

    public int ValueCount => _keys.Sum(k => k.Lines.Count);

    /// <summary>Adds a key header (once); <paramref name="path"/> as stored in snapshots, e.g. "HKLM\SOFTWARE\Vendor".</summary>
    public void AddKey(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Key(path);
    }

    /// <summary>Adds a value under <paramref name="keyPath"/>; a second value of the same name is ignored.</summary>
    public void AddValue(string keyPath, string name, uint type, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = Key(keyPath);
        if (key.Names.Add(name))
        {
            key.Lines.Add(FormatValue(name, type, data));
        }
    }

    /// <summary>A line that deletes the value on import (used in backups for values that did not exist).</summary>
    public void AddValueDeletion(string keyPath, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = Key(keyPath);
        if (key.Names.Add(name))
        {
            key.Lines.Add(QuoteName(name) + "=-");
        }
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Header).Append("\r\n\r\n");
        foreach (var (key, lines, _) in _keys)
        {
            text.Append('[').Append(KeyName(key)).Append("]\r\n");
            foreach (var line in lines)
            {
                text.Append(line).Append("\r\n");
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Writes the file (UTF-16 LE with BOM), replacing an existing one.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
    }

    /// <summary>"HKLM\X" → "HKEY_LOCAL_MACHINE\X", "HKU\X" → "HKEY_USERS\X".</summary>
    public static string KeyName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        var head = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? string.Empty : path[slash..];
        var full = head.ToUpperInvariant() switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKU" => "HKEY_USERS",
            "HKCU" => "HKEY_CURRENT_USER",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKCC" => "HKEY_CURRENT_CONFIG",
            _ => head,
        };
        return full + rest;
    }

    public static string FormatValue(string name, uint type, ReadOnlySpan<byte> data)
    {
        var prefix = QuoteName(name) + "=";
        if (type == ItemFormat.RegSz && TryGetPlainString(data, out var text))
        {
            return prefix + "\"" + Escape(text) + "\"";
        }

        if (type == ItemFormat.RegDword && data.Length == 4)
        {
            return prefix + "dword:" + BinaryPrimitives.ReadUInt32LittleEndian(data).ToString("x8", CultureInfo.InvariantCulture);
        }

        var hexType = type == ItemFormat.RegBinary ? "hex:" : string.Create(CultureInfo.InvariantCulture, $"hex({type:x}):");
        return HexLines(prefix + hexType, data);
    }

    /// <summary>Hex bytes wrapped like Regedit: lines up to 80 characters ending in "\" and continued after two spaces.</summary>
    private static string HexLines(string prefix, ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder(prefix, prefix.Length + (data.Length * 3) + 16);
        var lineLength = prefix.Length;
        for (var i = 0; i < data.Length; i++)
        {
            text.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            lineLength += 2;
            if (i == data.Length - 1)
            {
                break;
            }

            text.Append(',');
            lineLength++;
            if (lineLength + 3 > LineWidth - 1)
            {
                text.Append("\\\r\n  ");
                lineLength = 2;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// True when the data is UTF-16 text with exactly one terminating null and no other nulls — then
    /// <c>"text"</c> imports back to the same bytes.
    /// </summary>
    private static bool TryGetPlainString(ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;
        if (data.Length < 2 || data.Length % 2 != 0 || data[^1] != 0 || data[^2] != 0)
        {
            return false;
        }

        var value = Encoding.Unicode.GetString(data[..^2]);
        if (value.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0 || !Encoding.Unicode.GetBytes(value).AsSpan().SequenceEqual(data[..^2]))
        {
            return false;
        }

        text = value;
        return true;
    }

    private static string QuoteName(string name) => name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private (string Key, List<string> Lines, HashSet<string> Names) Key(string path)
    {
        if (!_index.TryGetValue(path, out var i))
        {
            i = _keys.Count;
            _keys.Add((path, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            _index[path] = i;
        }

        return _keys[i];
    }
}
