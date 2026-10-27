using System.Text;

namespace SysStateTracer.Core.Ignore;

/// <summary>Result of reading the remote list. <see cref="Text"/> is null when it could not be used.</summary>
public sealed record RemoteIgnoreFetch(string? Text, string Message)
{
    public bool Succeeded => Text is not null;
}

/// <summary>
/// Reads the remote Global Ignore list (decision F1) from an http(s) URL or a file path, including
/// network shares (<c>\\server\share\list.txt</c>), with a time limit and a size limit.
/// Never throws for network or file problems; the caller then uses the local list.
/// </summary>
public static class RemoteIgnoreSource
{
    /// <summary>The list kept in this project's repository.</summary>
    public const string DefaultLocation = "https://raw.githubusercontent.com/IgorZaharov/SysStateTracer/main/ignore/SysStateTracer.ignore.txt";

    public const int MaxBytes = 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>True for an http/https URL or a rooted file path; false for anything else.</summary>
    public static bool IsValidLocation(string? location, out string message)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            message = "No remote location is set.";
            return false;
        }

        location = location.Trim();
        if (TryGetHttpUri(location, out _) || IsFilePath(location))
        {
            message = string.Empty;
            return true;
        }

        message = "The remote location must be an http:// or https:// address, or a file path such as \\\\server\\share\\SysStateTracer.ignore.txt.";
        return false;
    }

    public static async Task<RemoteIgnoreFetch> FetchAsync(
        string? location, HttpClient http, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!IsValidLocation(location, out var invalid))
        {
            return new RemoteIgnoreFetch(null, invalid);
        }

        location = location!.Trim();
        var limit = timeout ?? DefaultTimeout;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);

        byte[]? bytes;
        try
        {
            bytes = TryGetHttpUri(location, out var uri)
                ? await DownloadAsync(http, uri, timer.Token).ConfigureAwait(false)
                : await ReadFileAsync(location, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteIgnoreFetch(null, $"{location} did not answer within {limit.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or RemoteListException)
        {
            return new RemoteIgnoreFetch(null, $"{location} could not be read: {ex.Message}");
        }

        if (bytes is null)
        {
            return new RemoteIgnoreFetch(null, $"{location} is larger than {MaxBytes / 1024} KB.");
        }

        var text = Decode(bytes);
        var (rules, errors) = IgnoreRuleParser.ParseText(text, IgnoreRuleSource.Remote);
        if (rules.Count == 0 && errors.Count > 0)
        {
            return new RemoteIgnoreFetch(null, $"{location} does not contain ignore rules.");
        }

        var note = errors.Count > 0 ? $" ({errors.Count} lines not understood)" : string.Empty;
        return new RemoteIgnoreFetch(text, $"Read {rules.Count} rules from {location}{note}.");
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteListException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a local or network file. Opening a path on an unreachable server can block for a long time,
    /// so the work runs on the thread pool and the wait is limited by <paramref name="cancellationToken"/>.
    /// </summary>
    private static Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var read = Task.Run(
            async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
                if (stream.Length > MaxBytes)
                {
                    return null;
                }

                return await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
            },
            CancellationToken.None);
        return read.WaitAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool TryGetHttpUri(string location, out Uri uri)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>"\\server\share\x.txt" or "C:\Lists\x.txt" (not relative paths, not other URI schemes).</summary>
    private static bool IsFilePath(string location) =>
        location.StartsWith(@"\\", StringComparison.Ordinal) ||
        (location.Length >= 3 && char.IsAsciiLetter(location[0]) && location[1] == ':' && location[2] == '\\');

    private sealed class RemoteListException(string message) : Exception(message);
}
