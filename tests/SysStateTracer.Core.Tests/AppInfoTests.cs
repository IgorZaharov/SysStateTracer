namespace SysStateTracer.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
    }

    [Theory]
    [InlineData("0.1.0+aa7333590979f517e4002bc71fc6fb1dd1da389c", "0.1.0+aa73335")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc", "1.2.3+abc")]
    [InlineData(null, "0.0.0")]
    [InlineData("", "0.0.0")]
    public void ShortenVersion_KeepsSevenHashCharacters(string? input, string expected)
    {
        Assert.Equal(expected, AppInfo.ShortenVersion(input));
    }

    [Fact]
    public void GitHubUrl_PointsToTheRepository()
    {
        Assert.Equal("https://github.com/IgorZaharov/SysStateTracer", AppInfo.GitHubUrl);
    }
}
