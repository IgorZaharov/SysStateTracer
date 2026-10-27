using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Tests.Model;

public class NameTableTests
{
    [Fact]
    public void Add_ReturnsSameIdForSameName()
    {
        var names = new NameTable(capacity: 2);

        var a = names.Add("Windows");
        var b = names.Add("System32");
        var again = names.Add("Windows");

        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.Equal(2, names.Count);
        Assert.Equal("System32", names.GetString(b));
    }

    [Fact]
    public void Add_IsCaseSensitive_SoNamesKeepTheirCase()
    {
        var names = new NameTable();

        Assert.NotEqual(names.Add("Boot"), names.Add("boot"));
    }

    [Fact]
    public void Add_GrowsBeyondInitialCapacity()
    {
        var names = new NameTable(capacity: 16);
        for (var i = 0; i < 10_000; i++)
        {
            names.Add($"name-{i}");
        }

        Assert.Equal(10_000, names.Count);
        Assert.Equal("name-9999", names.GetString(9999));
        Assert.Equal(string.Empty, names.GetString(names.Add(string.Empty)));
    }

    [Fact]
    public void Freeze_MakesTableReadOnly()
    {
        var names = new NameTable();
        var id = names.Add("x");
        names.Freeze();

        Assert.True(names.IsFrozen);
        Assert.Equal("x", names.GetString(id));
        Assert.Throws<InvalidOperationException>(() => names.Add("y"));
    }

    [Theory]
    [InlineData("apps", "Boot", -1)]
    [InlineData("Boot", "apps", 1)]
    [InlineData("BOOT", "boot", -1)]
    [InlineData("boot", "boot", 0)]
    [InlineData("a", "ab", -1)]
    public void Compare_IsCaseInsensitiveFirst(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NameTable.Compare(a, b)));
    }
}
