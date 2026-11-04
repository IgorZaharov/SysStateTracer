using System.Security;
using Microsoft.Win32;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Scanning.Env;

/// <summary>
/// Reads the System and User environment variables from where Windows keeps them (the keys the Environment
/// Variables window edits). Values are read as written, without expanding %VAR% references, so a change from
/// "%SystemRoot%\x" to "C:\Windows\x" is seen. The user is the one running SysStateTracer.
/// </summary>
public static class EnvironmentScanner
{
    /// <summary>The System variables' key below HKLM.</summary>
    public const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>The user's variables' key below HKCU.</summary>
    public const string UserKey = "Environment";

    /// <summary>The key a scope's variables are set in, e.g. "HKCU\Environment" (Jump to opens it in Regedit).</summary>
    public static string RegistryKeyPath(EnvironmentScope scope) =>
        scope == EnvironmentScope.User ? @"HKCU\" + UserKey : @"HKLM\" + SystemKey;

    /// <summary>Adds every System and User variable not left out by an ENV: rule; returns how many were added.</summary>
    public static int Scan(EnvironmentTableBuilder builder, IgnoreMatcher ignore, SkippedCollector skipped)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(ignore);
        ArgumentNullException.ThrowIfNull(skipped);
        var count = Read(builder, ignore, skipped, EnvironmentScope.System, RegistryHive.LocalMachine, SystemKey);
        count += Read(builder, ignore, skipped, EnvironmentScope.User, RegistryHive.CurrentUser, UserKey);
        return count;
    }

    private static int Read(EnvironmentTableBuilder builder, IgnoreMatcher ignore, SkippedCollector skipped, EnvironmentScope scope, RegistryHive hive, string keyPath)
    {
        var count = 0;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(keyPath, writable: false);
            if (key is null)
            {
                return 0;
            }

            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0 || ignore.IsIgnored(IgnoreTarget.Environment, EnvironmentTable.ScopeName(scope) + "\\" + name))
                {
                    continue; // the default value is not a variable
                }

                var kind = key.GetValueKind(name);
                var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var text = data switch
                {
                    string s => s,
                    string[] lines => string.Join(";", lines),
                    null => string.Empty,
                    _ => Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                };
                builder.Add(scope, name, text, (uint)(int)kind);
                count++;
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            skipped.Add((hive == RegistryHive.CurrentUser ? @"HKCU\" : @"HKLM\") + keyPath, ex.HResult & 0xFFFF);
        }

        return count;
    }
}
