using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SysStateTracer.Core.Model;

/// <summary>What a permission set applies to: the meaning of the rights bits differs.</summary>
public enum PermissionTarget
{
    FileSystem,
    Registry,
}

/// <summary>One explicit access-control entry, in words.</summary>
public sealed record PermissionEntry(bool Allow, string Sid, int Rights, InheritanceFlags Inheritance, PropagationFlags Propagation)
{
    /// <summary>"Allow · Users · Modify".</summary>
    public string Describe(PermissionTarget target) =>
        $"{(Allow ? "Allow" : "Deny")} · {Permissions.AccountName(Sid)} · {Permissions.DescribeRights(Rights, target)}";

    /// <summary>"this folder, subfolders and files", "this key and subkeys", …</summary>
    public string DescribeAppliesTo(PermissionTarget target) => Permissions.DescribeAppliesTo(Inheritance, Propagation, target);
}

/// <summary>An item's own permissions: owner, whether inheritance is disabled, and the explicit entries.</summary>
public sealed record PermissionSet(string? OwnerSid, bool InheritanceDisabled, bool NoDacl, IReadOnlyList<PermissionEntry> Entries)
{
    public bool HasExplicitEntries => Entries.Count > 0;
}

/// <summary>
/// Permissions as SysStateTracer records them (decision "Permission changes"): only what was set on the item
/// itself — owner, the "inheritance disabled" flag and the explicit (not inherited) entries of the DACL — as a
/// self-relative security descriptor in Base64. Binary rather than SDDL, because SDDL writes some SIDs as
/// aliases (DA, DU, LA) that mean other accounts on another computer. Inherited entries are left out, so a
/// change made on a folder and passed down to its contents shows only on that folder. The SACL is not read.
/// </summary>
public static class Permissions
{
    private static readonly ConcurrentDictionary<string, string> AccountNames = new(StringComparer.Ordinal);

    /// <summary>The recorded form of a self-relative security descriptor (owner and DACL at least).</summary>
    public static string Normalize(ReadOnlySpan<byte> securityDescriptor)
    {
        var raw = new RawSecurityDescriptor(securityDescriptor.ToArray(), 0);
        return Normalize(raw);
    }

    /// <summary>The recorded form of an SDDL string (used by tests and for descriptors given as text).</summary>
    public static string NormalizeSddl(string sddl) => Normalize(new RawSecurityDescriptor(sddl));

    private static string Normalize(RawSecurityDescriptor raw)
    {
        RawAcl? dacl = null;
        if (raw.DiscretionaryAcl is { } source)
        {
            dacl = new RawAcl(source.Revision, source.Count);
            foreach (var ace in source)
            {
                if ((ace.AceFlags & AceFlags.Inherited) == 0)
                {
                    dacl.InsertAce(dacl.Count, ace);
                }
            }
        }

        var flags = ControlFlags.SelfRelative;
        if (raw.DiscretionaryAcl is not null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0)
        {
            flags |= ControlFlags.DiscretionaryAclPresent;
        }

        flags |= raw.ControlFlags & ControlFlags.DiscretionaryAclProtected;
        var normalized = new RawSecurityDescriptor(flags, raw.Owner, null, null, dacl);
        var bytes = new byte[normalized.BinaryLength];
        normalized.GetBinaryForm(bytes, 0);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>SDDL of a recorded permission string, for reading (tests, logs); null when it is not valid.</summary>
    public static string? ToSddl(string? recorded) =>
        TryRaw(recorded)?.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    private static RawSecurityDescriptor? TryRaw(string? recorded)
    {
        if (string.IsNullOrEmpty(recorded))
        {
            return null;
        }

        try
        {
            return new RawSecurityDescriptor(Convert.FromBase64String(recorded), 0);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Parses a recorded permission string; null when it is not valid SDDL.</summary>
    public static PermissionSet? Parse(string? recorded)
    {
        if (TryRaw(recorded) is not { } raw)
        {
            return null;
        }

        var entries = new List<PermissionEntry>();
        if (raw.DiscretionaryAcl is { } dacl)
        {
            foreach (var ace in dacl)
            {
                if (ace is CommonAce common &&
                    common.AceQualifier is AceQualifier.AccessAllowed or AceQualifier.AccessDenied)
                {
                    entries.Add(new PermissionEntry(
                        common.AceQualifier == AceQualifier.AccessAllowed,
                        common.SecurityIdentifier.Value,
                        common.AccessMask,
                        common.InheritanceFlags,
                        common.PropagationFlags));
                }
            }
        }

        var noDacl = (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && raw.DiscretionaryAcl is null;
        return new PermissionSet(
            raw.Owner?.Value,
            (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            noDacl,
            entries);
    }

    /// <summary>The entries of <paramref name="newer"/> that <paramref name="older"/> lacks (added), and the reverse.</summary>
    public static (IReadOnlyList<PermissionEntry> Added, IReadOnlyList<PermissionEntry> Removed) Difference(PermissionSet? older, PermissionSet? newer)
    {
        var a = older?.Entries ?? [];
        var b = newer?.Entries ?? [];
        return (b.Where(e => !a.Contains(e)).ToList(), a.Where(e => !b.Contains(e)).ToList());
    }

    /// <summary>
    /// One short line for the list and the report, e.g. "Owner changed", "+ Allow · Users · Modify",
    /// "Inheritance disabled, +3 entries".
    /// </summary>
    public static string Summarize(string? olderRecorded, string? newerRecorded, PermissionTarget target)
    {
        var older = Parse(olderRecorded);
        var newer = Parse(newerRecorded);
        var parts = new List<string>();
        if (older?.OwnerSid != newer?.OwnerSid)
        {
            parts.Add("Owner changed");
        }

        if ((older?.InheritanceDisabled ?? false) != (newer?.InheritanceDisabled ?? false))
        {
            parts.Add(newer?.InheritanceDisabled == true ? "Inheritance disabled" : "Inheritance enabled");
        }

        var (added, removed) = Difference(older, newer);
        if (added.Count + removed.Count == 1)
        {
            parts.Add(added.Count == 1 ? "+ " + added[0].Describe(target) : "− " + removed[0].Describe(target));
        }
        else
        {
            if (added.Count > 0)
            {
                parts.Add($"+{added.Count} entries");
            }

            if (removed.Count > 0)
            {
                parts.Add($"−{removed.Count} entries");
            }
        }

        return parts.Count == 0 ? "Changed" : string.Join(", ", parts);
    }

    /// <summary>One snapshot's side for reports: "owner SYSTEM, inheritance disabled, own: Allow · Users · Modify".</summary>
    public static string Brief(string? recorded, PermissionTarget target)
    {
        if (Parse(recorded) is not { } set)
        {
            return "permissions not read";
        }

        var parts = new List<string> { "owner " + AccountName(set.OwnerSid) };
        if (set.InheritanceDisabled)
        {
            parts.Add("inheritance disabled");
        }

        parts.Add(set.Entries.Count == 0 ? "no own entries" : "own: " + string.Join("; ", set.Entries.Select(e => e.Describe(target))));
        return string.Join(", ", parts);
    }

    /// <summary>"Inherited" (no own entries, inheritance on) or "Own" for the list's Permissions column.</summary>
    public static string DescribeKind(string? recorded)
    {
        var set = Parse(recorded);
        return set is null ? string.Empty
            : set.HasExplicitEntries || set.InheritanceDisabled || set.NoDacl ? "Own" : "Inherited";
    }

    /// <summary>True when the item has permissions of its own (explicit entries or inheritance disabled).</summary>
    public static bool HasOwn(string? recorded) => DescribeKind(recorded) == "Own";

    /// <summary>"BUILTIN\Administrators" for a SID on this computer, or the SID itself when unknown.</summary>
    public static string AccountName(string? sid)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return "(none)";
        }

        return AccountNames.GetOrAdd(sid, static s =>
        {
            try
            {
                return new SecurityIdentifier(s).Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                return s;
            }
        });
    }

    /// <summary>Rights in the words of the Security tab: Full control, Modify, Read &amp; execute, Read, Write, or Special.</summary>
    public static string DescribeRights(int rights, PermissionTarget target)
    {
        const int genericAll = 0x10000000;
        const int genericExecute = 0x20000000;
        const int genericWrite = 0x40000000;
        const int genericRead = unchecked((int)0x80000000);
        if ((rights & genericAll) != 0)
        {
            return "Full control";
        }

        if (target == PermissionTarget.Registry)
        {
            rights |= (rights & genericRead) != 0 ? 0x20019 : 0;
            rights |= (rights & genericWrite) != 0 ? 0x20006 : 0;
            rights &= 0x0FFFFFFF;
            return rights switch
            {
                _ when (rights & 0xF003F) == 0xF003F => "Full control",
                _ when (rights & 0x20019) == 0x20019 && (rights & 0x20006) == 0x20006 => "Read, Write",
                _ when (rights & 0x20019) == 0x20019 => "Read",
                _ when (rights & 0x20006) == 0x20006 => "Write",
                _ => $"Special (0x{rights:X})",
            };
        }

        rights |= (rights & genericRead) != 0 ? 0x120089 : 0;
        rights |= (rights & genericWrite) != 0 ? 0x120116 : 0;
        rights |= (rights & genericExecute) != 0 ? 0x1200A0 : 0;
        rights &= 0x0FFFFFFF;
        return rights switch
        {
            _ when (rights & 0x1F01FF) == 0x1F01FF => "Full control",
            _ when (rights & 0x1301BF) == 0x1301BF => "Modify",
            _ when (rights & 0x1200A9) == 0x1200A9 && (rights & 0x116) == 0x116 => "Read & execute, Write",
            _ when (rights & 0x1200A9) == 0x1200A9 => "Read & execute",
            _ when (rights & 0x120089) == 0x120089 && (rights & 0x116) == 0x116 => "Read, Write",
            _ when (rights & 0x120089) == 0x120089 => "Read",
            _ when (rights & 0x116) == 0x116 => "Write",
            _ => $"Special (0x{rights:X})",
        };
    }

    /// <summary>The "Applies to" text of the Advanced Security window.</summary>
    public static string DescribeAppliesTo(InheritanceFlags inheritance, PropagationFlags propagation, PermissionTarget target)
    {
        var containers = (inheritance & InheritanceFlags.ContainerInherit) != 0;
        var objects = (inheritance & InheritanceFlags.ObjectInherit) != 0;
        var inheritOnly = (propagation & PropagationFlags.InheritOnly) != 0;
        var oneLevel = (propagation & PropagationFlags.NoPropagateInherit) != 0 ? " (one level)" : string.Empty;
        if (target == PermissionTarget.Registry)
        {
            return (containers, inheritOnly) switch
            {
                (false, _) => "this key only",
                (true, false) => "this key and subkeys" + oneLevel,
                (true, true) => "subkeys only" + oneLevel,
            };
        }

        var text = (inheritOnly, containers, objects) switch
        {
            (false, false, false) => "this folder only",
            (false, true, true) => "this folder, subfolders and files",
            (false, true, false) => "this folder and subfolders",
            (false, false, true) => "this folder and files",
            (true, true, true) => "subfolders and files only",
            (true, true, false) => "subfolders only",
            (true, false, true) => "files only",
            _ => "this object only",
        };
        return text + oneLevel;
    }
}
