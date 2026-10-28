using System.Net;
using System.Text;
using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Tests.Ignore;

/// <summary>Decision F1: Local file beside the exe, or a remote URL / network file with fallback to Local.</summary>
public class IgnoreListSourceTests
{
    private const string Url = "https://example.com/ignore.txt";
    private const string RemoteText = "# shared\nFS:C:\\Shared\nSVC:SharedSvc\n";

    [Fact]
    public void LocalFile_IsCreatedFromDefaults()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));

        var list = local.Load();

        Assert.True(File.Exists(local.Path));
        Assert.Equal(DefaultIgnoreRules.Text, File.ReadAllText(local.Path));
        Assert.Equal(DefaultIgnoreRules.RuleLines.Count, list.Rules.Count);
        Assert.Empty(list.Errors);
    }

    [Fact]
    public void LocalFile_RemovedRulesStayRemoved()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));
        local.Save("FS:D:\\Mine\n");

        var list = local.Load();

        Assert.Equal("D:\\Mine", Assert.Single(list.Rules).Pattern);
    }

    [Fact]
    public void AddRule_AppendsOnceAndKeepsComments()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));
        local.Save("# mine\nFS:D:\\Mine");

        Assert.True(local.AddRule(@"REG:HKLM\SOFTWARE\Vendor\LastRun"));
        Assert.False(local.AddRule(@"reg:hklm\software\vendor\lastrun\"));
        Assert.False(local.AddRule(@"FS:D:\Mine"));

        var text = File.ReadAllText(local.Path);
        Assert.StartsWith("# mine\nFS:D:\\Mine", text, StringComparison.Ordinal);
        Assert.Equal(2, local.Load().Rules.Count);
    }

    [Fact]
    public void AddRule_RejectsInvalidLine()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));

        Assert.Throws<ArgumentException>(() => local.AddRule("not a rule"));
    }

    [Theory]
    [InlineData("https://example.com/list.txt", true)]
    [InlineData("http://intranet/list.txt", true)]
    [InlineData(@"\\server\share\list.txt", true)]
    [InlineData(@"D:\Lists\list.txt", true)]
    [InlineData("ftp://example.com/list.txt", false)]
    [InlineData("list.txt", false)]
    [InlineData("  ", false)]
    public void IsValidLocation_AcceptsUrlsAndRootedPaths(string location, bool valid)
    {
        Assert.Equal(valid, RemoteIgnoreSource.IsValidLocation(location, out _));
    }

    [Fact]
    public async Task Fetch_Url_ReturnsRules()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Ok(RemoteText))));

        var result = await RemoteIgnoreSource.FetchAsync(Url, http);

        Assert.True(result.Succeeded);
        Assert.Equal(RemoteText, result.Text);
        Assert.Contains("2 rules", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_File_ReturnsRules()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("shared.txt"), RemoteText);
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("no network for files")));

        var result = await RemoteIgnoreSource.FetchAsync(dir.File("shared.txt"), http);

        Assert.True(result.Succeeded);
        Assert.Equal(RemoteText, result.Text);
    }

    [Fact]
    public async Task Fetch_MissingFile_Fails()
    {
        using var dir = new TempDirectory();
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Ok(RemoteText))));

        var result = await RemoteIgnoreSource.FetchAsync(dir.File("missing.txt"), http);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be read", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Fetch_ServerError_Fails(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));

        var result = await RemoteIgnoreSource.FetchAsync(Url, http);

        Assert.False(result.Succeeded);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_TooLarge_Fails()
    {
        var big = new string('#', RemoteIgnoreSource.MaxBytes + 10);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Ok(big))));

        var result = await RemoteIgnoreSource.FetchAsync(Url, http);

        Assert.False(result.Succeeded);
        Assert.Contains("larger than", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_NotARuleList_Fails()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Ok("<html><body>Login required</body></html>"))));

        var result = await RemoteIgnoreSource.FetchAsync(Url, http);

        Assert.False(result.Succeeded);
        Assert.Contains("does not contain ignore rules", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_SlowServer_TimesOut()
    {
        using var http = new HttpClient(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(RemoteText);
        }));

        var result = await RemoteIgnoreSource.FetchAsync(Url, http, TimeSpan.FromMilliseconds(200));

        Assert.False(result.Succeeded);
        Assert.Contains("did not answer", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_Remote_UsesRemoteAndLocalRulesTogether()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));
        local.Save("FS:D:\\Mine\n");
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Ok(RemoteText))));

        var state = await IgnoreListLoader.LoadAsync(IgnoreListSource.Remote, Url, local, http);

        Assert.Equal(IgnoreListOrigin.Remote, state.Origin);
        Assert.Equal(3, state.List.Rules.Count);
        Assert.Equal(2, state.List.Rules.Count(r => r.Source == IgnoreRuleSource.Remote));
        Assert.True(state.List.Matcher.IsIgnored(IgnoreTarget.FileSystem, @"D:\Mine\file.txt"));
    }

    [Fact]
    public async Task Loader_RemoteUnreachable_FallsBackToLocal()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));
        local.Save("FS:D:\\Mine\n");
        using var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("no route to host")));

        var state = await IgnoreListLoader.LoadAsync(IgnoreListSource.Remote, Url, local, http);

        Assert.Equal(IgnoreListOrigin.LocalFallback, state.Origin);
        Assert.Equal("D:\\Mine", Assert.Single(state.List.Rules).Pattern);
        Assert.Contains("local list", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_Local_NeverTouchesTheNetwork()
    {
        using var dir = new TempDirectory();
        var local = new LocalIgnoreFile(dir.File("SysStateTracer.ignore.txt"));
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(RemoteText)));
        using var http = new HttpClient(handler);

        var state = await IgnoreListLoader.LoadAsync(IgnoreListSource.Local, Url, local, http);

        Assert.Equal(IgnoreListOrigin.Local, state.Origin);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(DefaultIgnoreRules.RuleLines.Count, state.List.Rules.Count);
    }

    [Fact]
    public void RepositoryList_HasNoErrors()
    {
        // ignore/SysStateTracer.ignore.txt is the default Remote list; it must always parse cleanly.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SysStateTracer.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir.FullName, "ignore", "SysStateTracer.ignore.txt"));
        var list = GlobalIgnoreList.FromText(text, IgnoreRuleSource.Remote);

        Assert.Empty(list.Errors);
        Assert.True(list.Rules.Count >= 20, $"{list.Rules.Count} rules");
    }

    private static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }
}
