namespace SysStateTracer;

/// <summary>The application icon (embedded SysStateTracer.ico) for every window.</summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon?> Loaded = new(Load);

    /// <summary>The icon, or null if the resource is missing (windows then show the default icon).</summary>
    public static Icon? Value => Loaded.Value;

    private static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SysStateTracer.ico");
        return stream is null ? null : new Icon(stream);
    }
}
