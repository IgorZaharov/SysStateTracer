namespace SysStateTracer.Core.Model;

/// <summary>Whose environment variable: the computer's (System) or the user's who took the snapshot (User).</summary>
public enum EnvironmentScope : byte
{
    System,
    User,
}

/// <summary>
/// Environment variables part of a snapshot, as set in the registry (what installers change and what the
/// Environment Variables window shows): System variables from
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c> and the user's from
/// <c>HKCU\Environment</c>. Values are kept as written, not expanded (%SystemRoot% stays %SystemRoot%).
/// Sorted by scope (System first), then by name ignoring case. The value type is the registry type
/// (1 = REG_SZ, 2 = REG_EXPAND_SZ).
/// </summary>
public sealed class EnvironmentTable
{
    private readonly EnvironmentScope[] _scope;
    private readonly int[] _name;
    private readonly int[] _value;
    private readonly uint[] _type;

    internal EnvironmentTable(EnvironmentScope[] scope, int[] name, int[] value, uint[] type)
    {
        var n = name.Length;
        if (scope.Length != n || value.Length != n || type.Length != n)
        {
            throw new ArgumentException("Environment columns have different lengths.");
        }

        _scope = scope;
        _name = name;
        _value = value;
        _type = type;
    }

    public static EnvironmentTable Empty { get; } = new([], [], [], []);

    public int Count => _name.Length;

    public ReadOnlySpan<EnvironmentScope> Scope => _scope;

    public ReadOnlySpan<int> Name => _name;

    public ReadOnlySpan<int> Value => _value;

    public ReadOnlySpan<uint> ValueType => _type;

    /// <summary>"System" or "User".</summary>
    public static string ScopeName(EnvironmentScope scope) => scope == EnvironmentScope.User ? "User" : "System";

    /// <summary>"System\Path", "User\TEMP": how comparisons, ignore rules (ENV:) and paths name a variable.</summary>
    public string Key(int index, NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return ScopeName(_scope[index]) + "\\" + names.GetString(_name[index]);
    }
}

/// <summary>Collects environment variables in any order, then builds a sorted <see cref="EnvironmentTable"/>. Not thread-safe.</summary>
public sealed class EnvironmentTableBuilder
{
    private readonly NameTable _names;
    private readonly List<(EnvironmentScope Scope, int Name, int Value, uint Type)> _rows = [];

    public EnvironmentTableBuilder(NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names;
    }

    public int Count => _rows.Count;

    public void Add(EnvironmentScope scope, ReadOnlySpan<char> name, ReadOnlySpan<char> value, uint type)
    {
        _rows.Add((scope, _names.Add(name), _names.Add(value), type));
    }

    public EnvironmentTable Build()
    {
        var rows = _rows.ToArray();
        Array.Sort(rows, (a, b) => a.Scope != b.Scope ? a.Scope.CompareTo(b.Scope) : NameTable.Compare(_names[a.Name], _names[b.Name]));

        var n = rows.Length;
        var scope = new EnvironmentScope[n];
        var name = new int[n];
        var value = new int[n];
        var type = new uint[n];
        for (var i = 0; i < n; i++)
        {
            (scope[i], name[i], value[i], type[i]) = rows[i];
        }

        return new EnvironmentTable(scope, name, value, type);
    }
}
