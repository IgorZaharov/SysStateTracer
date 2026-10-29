using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// The right pane of the comparison window: the files of the selected folder, the values of the selected key,
/// or every service. With "Include subfolders" the files of every folder below are listed too, named relative
/// to the selected one ("lang\de-DE.xml"). Columns: change sign, name, then per kind size · date · attributes,
/// type · data, or display name · status · start type, each from the right-hand snapshot (the left one for
/// an item only there). Sorted by the column clicked; values that differ are highlighted. A row's id is its
/// place in the list as built.
/// </summary>
internal sealed class ComparisonListModel : ITreeGridModel
{
    /// <summary>The most rows listed (a whole drive with Include subfolders could have millions).</summary>
    public const int MaxRows = 250_000;

    public const int ChangeColumn = 0;
    public const int NameColumn = 1;

    private readonly List<Row> _rows = [];
    private long[] _order;

    public ComparisonListModel(ComparisonData data, ViewItem folder, bool includeSubfolders)
    {
        Data = data;
        Folder = folder;
        Area = folder.Area;
        var folderPath = folder.IsCategory ? string.Empty : JoinPath(data.Path(folder));
        AddLeaves(folder, string.Empty, folderPath);
        if (includeSubfolders && !folder.IsCategory)
        {
            AddBelow(folder, string.Empty, folderPath);
        }

        _order = [.. Enumerable.Range(0, _rows.Count).Select(i => (long)i)];
        Sort(NameColumn, descending: false);
    }

    private readonly record struct Row(ViewItem Item, string Name, string FolderPath);

    public ComparisonData Data { get; }

    /// <summary>The folder, key or category listed.</summary>
    public ViewItem Folder { get; }

    public DiffArea Area { get; }

    /// <summary>More than <see cref="MaxRows"/> items: the rest are not listed.</summary>
    public bool Truncated { get; private set; }

    public int Count => _rows.Count;

    public IReadOnlyList<long> Roots => _order;

    public int SortColumn { get; private set; } = NameColumn;

    public bool SortDescending { get; private set; }

    /// <summary>The columns for an area: header, width, alignment.</summary>
    public static List<GridColumn> ColumnsFor(DiffArea area) => area switch
    {
        DiffArea.FileSystem =>
        [
            new(string.Empty, 32), new("Name", 320), new("Size", 100, HorizontalAlignment.Right), new("Date modified", 160), new("Attributes", 100),
        ],
        DiffArea.Registry =>
        [
            new(string.Empty, 32), new("Name", 260), new("Type", 130), new("Data", 420),
        ],
        _ =>
        [
            new(string.Empty, 32), new("Name", 200), new("Display name", 300), new("Status", 110), new("Start type", 160),
        ],
    };

    public ViewItem ItemOf(long id) => _rows[(int)id].Item;

    /// <summary>The name shown: relative to the selected folder with Include subfolders.</summary>
    public string NameOf(long id) => _rows[(int)id].Name;

    /// <summary>Full path: a file's path, "key\value" for a value, a service's name.</summary>
    public string PathOf(long id)
    {
        var row = _rows[(int)id];
        return Area == DiffArea.Services ? Data.Name(row.Item) : row.FolderPath + "\\" + Data.Name(row.Item);
    }

    /// <summary>What Jump to opens: the file in Explorer, or in Regedit the key holding the value.</summary>
    public string JumpPathOf(long id)
    {
        var row = _rows[(int)id];
        return Area switch
        {
            DiffArea.FileSystem => PathOf(id),
            DiffArea.Registry => row.FolderPath,
            _ => Data.Name(row.Item),
        };
    }

    /// <summary>A name for a row that stays the same when the snapshots are compared again.</summary>
    public string RowKey(long id) => _rows[(int)id].Name;

    public bool HasChildren(long id) => false;

    public IReadOnlyList<long> Children(long id) => [];

    public string Text(long id, int column)
    {
        var item = _rows[(int)id].Item;
        if (column == ChangeColumn)
        {
            return ComparisonData.Sign(item.Status);
        }

        if (column == NameColumn)
        {
            return Area == DiffArea.Services ? Data.Name(item) : NameText(id);
        }

        var (snapshot, index) = Data.Shown(item);
        switch (Area)
        {
            case DiffArea.FileSystem:
                var fs = snapshot.FileSystem;
                return column switch
                {
                    2 => ItemFormat.Size(fs.Size[index]),
                    3 => ItemFormat.Time(fs.LastWriteTime[index]),
                    _ => ItemFormat.Attributes(fs.Attributes[index]),
                };
            case DiffArea.Registry:
                var reg = snapshot.Registry;
                return column == 2 ? ItemFormat.ValueType(reg.ValueDataType[index]) : ItemFormat.ValueData(reg, index);
            default:
                var services = snapshot.Services;
                return column switch
                {
                    2 => snapshot.Names.GetString(services.DisplayName[index]),
                    3 => ItemFormat.ServiceState(services.State[index]),
                    _ => ItemFormat.StartType(services.StartType[index], services.Flags[index]),
                };
        }
    }

    private string NameText(long id)
    {
        var row = _rows[(int)id];
        if (Area == DiffArea.Registry && Data.Name(row.Item).Length == 0)
        {
            var slash = row.Name.LastIndexOf('\\');
            return slash < 0 ? "(Default)" : row.Name[..(slash + 1)] + "(Default)";
        }

        return row.Name;
    }

    public string? ToolTip(long id, int column) => PathOf(id);

    public GridRowStyle Style(long id)
    {
        var item = _rows[(int)id].Item;
        var marked = Data.IsMarked(item);
        var icon = Area switch
        {
            DiffArea.FileSystem => Glyphs.File,
            DiffArea.Registry => Glyphs.Value,
            _ => Glyphs.Settings,
        };
        return new GridRowStyle(ComparisonData.TextColor(item.Status), Strikeout: marked, Dim: marked, Bold: false, icon, ComparisonData.IconColor(item.Status));
    }

    public bool IsChangedCell(long id, int column)
    {
        var fields = Data.Fields(_rows[(int)id].Item);
        if (fields == ChangedFields.None)
        {
            return false;
        }

        return (Area, column) switch
        {
            (DiffArea.FileSystem, 2) => fields.HasFlag(ChangedFields.Size),
            (DiffArea.FileSystem, 3) => fields.HasFlag(ChangedFields.LastWriteTime),
            (DiffArea.FileSystem, 4) => fields.HasFlag(ChangedFields.Attributes),
            (DiffArea.Registry, 2) => fields.HasFlag(ChangedFields.ValueType),
            (DiffArea.Registry, 3) => fields.HasFlag(ChangedFields.ValueData),
            (DiffArea.Services, 2) => fields.HasFlag(ChangedFields.DisplayName),
            (DiffArea.Services, 3) => fields.HasFlag(ChangedFields.State),
            (DiffArea.Services, 4) => (fields & (ChangedFields.StartType | ChangedFields.ServiceFlags | ChangedFields.ServiceType)) != 0,
            _ => false,
        };
    }

    /// <summary>Sorts by a column; ties keep name order. Sizes and dates sort by value, the change column new → modified → deleted → same.</summary>
    public void Sort(int column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        var count = _rows.Count;
        var order = Enumerable.Range(0, count).ToArray();
        var names = _rows.Select(r => r.Name).ToArray();
        Comparison<int> byName = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(names[a], names[b]);
        Comparison<int> compare;
        if (column == NameColumn)
        {
            compare = byName;
        }
        else if (column == ChangeColumn)
        {
            var rank = _rows.Select(r => r.Item.Status switch
            {
                RowStatus.New => 0,
                RowStatus.Modified => 1,
                RowStatus.Deleted => 2,
                _ => 3,
            }).ToArray();
            compare = (a, b) => rank[a].CompareTo(rank[b]);
        }
        else if (Area == DiffArea.FileSystem && column is 2 or 3)
        {
            var keys = new long[count];
            for (var i = 0; i < count; i++)
            {
                var (snapshot, index) = Data.Shown(_rows[i].Item);
                keys[i] = column == 2 ? snapshot.FileSystem.Size[index] : snapshot.FileSystem.LastWriteTime[index];
            }

            compare = (a, b) => keys[a].CompareTo(keys[b]);
        }
        else
        {
            var keys = new string[count];
            for (var i = 0; i < count; i++)
            {
                keys[i] = Text(i, column);
            }

            compare = (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(keys[a], keys[b]);
        }

        var sign = descending ? -1 : 1;
        Array.Sort(order, (a, b) =>
        {
            var result = compare(a, b);
            if (result == 0)
            {
                result = byName(a, b);
            }

            return result == 0 ? a.CompareTo(b) : sign * result;
        });
        _order = [.. order.Select(i => (long)i)];
    }

    private void AddLeaves(ViewItem folder, string prefix, string folderPath)
    {
        foreach (var leaf in Data.Leaves(folder))
        {
            if (_rows.Count >= MaxRows)
            {
                Truncated = true;
                return;
            }

            _rows.Add(new Row(leaf, prefix + Data.Name(leaf), folderPath));
        }
    }

    /// <summary>The files or values of every folder or key below, depth first.</summary>
    private void AddBelow(ViewItem folder, string prefix, string folderPath)
    {
        foreach (var child in Data.ContainerChildren(folder))
        {
            if (_rows.Count >= MaxRows)
            {
                Truncated = true;
                return;
            }

            var name = Data.Name(child);
            var childPrefix = prefix + name + "\\";
            var childPath = folderPath + "\\" + name;
            AddLeaves(child, childPrefix, childPath);
            AddBelow(child, childPrefix, childPath);
        }
    }

    /// <summary>"C:\" → "C:", so names join as "C:\name".</summary>
    private static string JoinPath(string path) => path.Length == 3 && path[1] == ':' ? path[..2] : path;
}
