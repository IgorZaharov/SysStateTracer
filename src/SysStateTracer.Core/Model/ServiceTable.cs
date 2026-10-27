namespace SysStateTracer.Core.Model;

/// <summary>Extra start-type details shown by services.msc.</summary>
[Flags]
public enum ServiceFlags : byte
{
    None = 0,

    /// <summary>Automatic (Delayed Start).</summary>
    DelayedAutoStart = 1,

    /// <summary>Trigger Start.</summary>
    TriggerStart = 2,
}

/// <summary>
/// Services part of a snapshot, sorted by service name. Type, state and start type keep the
/// Service Control Manager's numeric values (e.g. state 4 = running, start type 2 = automatic).
/// </summary>
public sealed class ServiceTable
{
    private readonly int[] _name;
    private readonly int[] _displayName;
    private readonly uint[] _type;
    private readonly uint[] _state;
    private readonly uint[] _startType;
    private readonly ServiceFlags[] _flags;

    internal ServiceTable(int[] name, int[] displayName, uint[] type, uint[] state, uint[] startType, ServiceFlags[] flags)
    {
        var n = name.Length;
        if (displayName.Length != n || type.Length != n || state.Length != n || startType.Length != n || flags.Length != n)
        {
            throw new ArgumentException("Service columns have different lengths.");
        }

        _name = name;
        _displayName = displayName;
        _type = type;
        _state = state;
        _startType = startType;
        _flags = flags;
    }

    public static ServiceTable Empty { get; } = new([], [], [], [], [], []);

    public int Count => _name.Length;

    public ReadOnlySpan<int> Name => _name;

    public ReadOnlySpan<int> DisplayName => _displayName;

    public ReadOnlySpan<uint> ServiceType => _type;

    public ReadOnlySpan<uint> State => _state;

    public ReadOnlySpan<uint> StartType => _startType;

    public ReadOnlySpan<ServiceFlags> Flags => _flags;
}

/// <summary>Collects services in any order, then builds a name-sorted <see cref="ServiceTable"/>. Not thread-safe.</summary>
public sealed class ServiceTableBuilder
{
    private readonly NameTable _names;
    private readonly List<(int Name, int DisplayName, uint Type, uint State, uint StartType, ServiceFlags Flags)> _rows = [];

    public ServiceTableBuilder(NameTable names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names;
    }

    public int Count => _rows.Count;

    public void Add(ReadOnlySpan<char> name, ReadOnlySpan<char> displayName, uint type, uint state, uint startType, ServiceFlags flags)
    {
        _rows.Add((_names.Add(name), _names.Add(displayName), type, state, startType, flags));
    }

    public ServiceTable Build()
    {
        var rows = _rows.ToArray();
        Array.Sort(rows, (a, b) => NameTable.Compare(_names[a.Name], _names[b.Name]));

        var n = rows.Length;
        var name = new int[n];
        var displayName = new int[n];
        var type = new uint[n];
        var state = new uint[n];
        var startType = new uint[n];
        var flags = new ServiceFlags[n];
        for (var i = 0; i < n; i++)
        {
            (name[i], displayName[i], type[i], state[i], startType[i], flags[i]) = rows[i];
        }

        return new ServiceTable(name, displayName, type, state, startType, flags);
    }
}
