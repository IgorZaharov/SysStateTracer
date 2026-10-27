using System.Text;
using SysStateTracer.Core.Export;

namespace SysStateTracer.Core.Tests.Export;

public class RegFileTests
{
    private static byte[] Sz(string text) => Encoding.Unicode.GetBytes(text + "\0");

    [Fact]
    public void Formats_EveryValueTypeLikeRegedit()
    {
        Assert.Equal("\"Path\"=\"C:\\\\Program Files\\\\App\"", RegFile.FormatValue("Path", 1, Sz(@"C:\Program Files\App")));
        Assert.Equal("@=\"say \\\"hi\\\"\"", RegFile.FormatValue(string.Empty, 1, Sz("say \"hi\"")));
        Assert.Equal("\"Count\"=dword:0000002a", RegFile.FormatValue("Count", 4, BitConverter.GetBytes(42)));
        Assert.Equal("\"Big\"=hex(b):08,07,06,05,04,03,02,01", RegFile.FormatValue("Big", 11, BitConverter.GetBytes(0x0102030405060708L)));
        Assert.Equal("\"Bin\"=hex:00,ff,10", RegFile.FormatValue("Bin", 3, [0x00, 0xff, 0x10]));
        Assert.Equal("\"Empty\"=hex:", RegFile.FormatValue("Empty", 3, []));
        Assert.Equal("\"None\"=hex(0):", RegFile.FormatValue("None", 0, []));
        Assert.StartsWith("\"Expand\"=hex(2):25,00,77,00", RegFile.FormatValue("Expand", 2, Sz("%windir%")), StringComparison.Ordinal);
        Assert.Equal("\"Multi\"=hex(7):61,00,00,00,62,00,00,00,00,00", RegFile.FormatValue("Multi", 7, Encoding.Unicode.GetBytes("a\0b\0\0")));
        Assert.Equal("\"Odd\"=hex(12345):01", RegFile.FormatValue("Odd", 0x12345, [1]));
    }

    [Fact]
    public void Strings_ThatWouldNotRoundTrip_AreWrittenAsHex()
    {
        Assert.StartsWith("\"Embedded\"=hex(1):", RegFile.FormatValue("Embedded", 1, Encoding.Unicode.GetBytes("a\0b\0")), StringComparison.Ordinal);
        Assert.StartsWith("\"NoTerminator\"=hex(1):", RegFile.FormatValue("NoTerminator", 1, Encoding.Unicode.GetBytes("abc")), StringComparison.Ordinal);
        Assert.StartsWith("\"Lines\"=hex(1):", RegFile.FormatValue("Lines", 1, Sz("a\r\nb")), StringComparison.Ordinal);
        Assert.StartsWith("\"ShortDword\"=hex(4):", RegFile.FormatValue("ShortDword", 4, [1, 2]), StringComparison.Ordinal);
    }

    [Fact]
    public void LongHex_IsWrappedAt80Characters()
    {
        var text = RegFile.FormatValue("Data", 3, Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        var lines = text.Split("\r\n");

        Assert.True(lines.Length > 2);
        Assert.All(lines, l => Assert.True(l.Length <= 80, $"{l.Length}: {l}"));
        Assert.All(lines[..^1], l => Assert.EndsWith(",\\", l, StringComparison.Ordinal));
        Assert.All(lines[1..], l => Assert.StartsWith("  ", l, StringComparison.Ordinal));
        var bytes = string.Concat(lines.Select(l => l.TrimEnd('\\').Trim()))["\"Data\"=hex:".Length..].Split(',');
        Assert.Equal(200, bytes.Length);
    }

    [Fact]
    public void File_HasHeaderKeysValuesAndUtf16Bom()
    {
        var file = new RegFile();
        file.AddKey(@"HKLM\SOFTWARE\Vendor");
        file.AddValue(@"HKU\S-1-5-21-1\Software\App", "a", 1, Sz("x"));
        file.AddValue(@"HKU\S-1-5-21-1\Software\App", "A", 1, Sz("duplicate is ignored"));
        file.AddValueDeletion(@"HKU\S-1-5-21-1\Software\App", "gone");

        Assert.Equal(
            "Windows Registry Editor Version 5.00\r\n\r\n" +
            "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Vendor]\r\n\r\n" +
            "[HKEY_USERS\\S-1-5-21-1\\Software\\App]\r\n\"a\"=\"x\"\r\n\"gone\"=-\r\n\r\n",
            file.ToString());

        using var dir = new TempDirectory();
        var path = dir.File(@"out\export.reg");
        file.Save(path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal(file.ToString(), Encoding.Unicode.GetString(bytes[2..]));
    }
}
