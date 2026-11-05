using System.Reflection;

namespace SysStateTracer.Core;

/// <summary>Identity of the application, shown in Help/About and by <c>/?</c>.</summary>
public static class AppInfo
{
    public const string Name = "SysStateTracer";

    public const string GitHubUrl = "https://github.com/IgorZaharov/SysStateTracer";

    /// <summary>Version with a short commit hash, e.g. <c>0.1.0+aa73335</c>.</summary>
    public static string Version { get; } = ShortenVersion(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Cuts the full commit hash the SDK appends after '+' down to 7 characters.</summary>
    internal static string ShortenVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "0.0.0";
        }

        const int ShortHashLength = 7;
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && informationalVersion.Length > plus + 1 + ShortHashLength
            ? informationalVersion[..(plus + 1 + ShortHashLength)]
            : informationalVersion;
    }
}
