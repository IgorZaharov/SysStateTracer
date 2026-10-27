namespace SysStateTracer.Core.Ignore;

/// <summary>Where the list in effect came from.</summary>
public enum IgnoreListOrigin
{
    /// <summary>Local was selected.</summary>
    Local,

    /// <summary>Remote was selected and could be read.</summary>
    Remote,

    /// <summary>Remote was selected but could not be read, so the local list is in effect.</summary>
    LocalFallback,
}

/// <summary>The Global Ignore list in effect, with where it came from and a line for Preferences / the log.</summary>
public sealed record IgnoreListState(GlobalIgnoreList List, IgnoreListOrigin Origin, string Message);

/// <summary>Picks the Global Ignore list according to Preferences (decision F1).</summary>
public static class IgnoreListLoader
{
    public static async Task<IgnoreListState> LoadAsync(
        IgnoreListSource source,
        string? remoteLocation,
        LocalIgnoreFile local,
        HttpClient http,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(http);

        if (source == IgnoreListSource.Remote)
        {
            var fetch = await RemoteIgnoreSource.FetchAsync(remoteLocation, http, timeout, cancellationToken).ConfigureAwait(false);
            if (fetch.Succeeded)
            {
                return new IgnoreListState(GlobalIgnoreList.FromText(fetch.Text!, IgnoreRuleSource.Remote), IgnoreListOrigin.Remote, fetch.Message);
            }

            var fallback = LoadLocal(local);
            return new IgnoreListState(
                fallback,
                IgnoreListOrigin.LocalFallback,
                $"{fetch.Message} Using the local list ({fallback.Rules.Count} rules) instead.");
        }

        var list = LoadLocal(local);
        return new IgnoreListState(list, IgnoreListOrigin.Local, $"Using the local list ({list.Rules.Count} rules).");
    }

    /// <summary>The local file's rules; the built-in defaults when the file cannot be read.</summary>
    private static GlobalIgnoreList LoadLocal(LocalIgnoreFile local)
    {
        try
        {
            return local.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Config.Log.Warning($"Could not read the local ignore list {local.Path}; using the built-in rules.", ex);
            return GlobalIgnoreList.FromText(DefaultIgnoreRules.Text, IgnoreRuleSource.Local);
        }
    }
}
