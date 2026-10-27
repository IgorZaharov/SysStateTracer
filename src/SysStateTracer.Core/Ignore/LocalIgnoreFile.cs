using System.Text;

namespace SysStateTracer.Core.Ignore;

/// <summary>Which Global Ignore list is in effect (decision F1).</summary>
public enum IgnoreListSource
{
    /// <summary>SysStateTracer.ignore.txt beside the exe.</summary>
    Local,

    /// <summary>A URL or network file; the local list is used when it cannot be read.</summary>
    Remote,
}

/// <summary>
/// The local Global Ignore list: SysStateTracer.ignore.txt beside the exe (decision F1), plain text in
/// the syntax of docs/ignore-list.md. Created from the built-in default rules when it does not exist.
/// "Add to Global Ignore list" in the comparison window always adds here.
/// </summary>
public sealed class LocalIgnoreFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public LocalIgnoreFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    /// <summary>The file's text; the file is first created from the built-in defaults when missing.</summary>
    public string ReadText()
    {
        if (!File.Exists(Path))
        {
            try
            {
                Save(DefaultIgnoreRules.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Config.Log.Warning($"Could not create the local ignore list: {Path}", ex);
                return DefaultIgnoreRules.Text;
            }
        }

        return File.ReadAllText(Path);
    }

    public GlobalIgnoreList Load() => GlobalIgnoreList.FromText(ReadText(), IgnoreRuleSource.Local);

    /// <summary>Replaces the whole file (Preferences → Edit), atomically.</summary>
    public void Save(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, text, Utf8NoBom);
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>
    /// Appends a rule ("Add to Global Ignore list"). Returns false when the same rule
    /// (ignoring case and a trailing backslash) is already in the file.
    /// </summary>
    public bool AddRule(string line)
    {
        if (!IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out var rule, out var error))
        {
            throw new ArgumentException(error, nameof(line));
        }

        var text = ReadText();
        var (existing, _) = IgnoreRuleParser.ParseText(text, IgnoreRuleSource.Local);
        if (existing.Any(r => r.Target == rule.Target && string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : Environment.NewLine;
        Save(text + separator + rule.Text.Trim() + Environment.NewLine);
        return true;
    }
}
