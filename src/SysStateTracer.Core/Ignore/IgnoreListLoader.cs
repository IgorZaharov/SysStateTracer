namespace SysStateTracer.Core.Ignore;

/// <summary>Where the rules in effect came from.</summary>
public enum IgnoreListOrigin
{
    /// <summary>Local only was chosen.</summary>
    Local,

    /// <summary>The local and the remote list, both read.</summary>
    Remote,

    /// <summary>The remote list could not be read, so only the local list is in effect.</summary>
    LocalFallback,
}

/// <summary>The Global Ignore list in effect, with where it came from and a line for Preferences / the log.</summary>
public sealed record IgnoreListState(GlobalIgnoreList List, IgnoreListOrigin Origin, string Message);

/// <summary>
/// The Global Ignore rules according to Preferences: the local list, plus the remote list unless "local only"
/// is chosen (F22). Rules added in the comparison window go to the local list, so they always apply.
/// </summary>
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
                var own = LoadLocal(local);
                var remote = GlobalIgnoreList.FromText(fetch.Text!, IgnoreRuleSource.Remote);
                return new IgnoreListState(
                    GlobalIgnoreList.Combine(own, remote),
                    IgnoreListOrigin.Remote,
                    $"{fetch.Message} Using it ({remote.Rules.Count} rules) together with the local list ({own.Rules.Count} rules).");
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
