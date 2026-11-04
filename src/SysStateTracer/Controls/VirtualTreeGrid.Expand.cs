namespace SysStateTracer.Controls;

/// <summary>
/// Expanding and collapsing a whole branch, or one level of it at a time: the comparison tree's context menu
/// (Expand all / Collapse all) and its keys + − * / (as in Explorer and Regedit; + and − go one level deeper
/// or shallower with each press). Each operation rebuilds only the rows of the branch, in one step.
/// </summary>
internal sealed partial class VirtualTreeGrid
{
    /// <summary>
    /// The keys + − * / expand and collapse the focused row's branch (<see cref="ExpandLevel"/>,
    /// <see cref="CollapseLevel"/>, <see cref="ExpandAll"/>, <see cref="CollapseAll"/>). Off by default.
    /// </summary>
    public bool BranchKeys { get; set; }

    /// <summary>
    /// Expand all stops before the tree would have more rows than this (Show all on a whole drive has millions);
    /// what it did not reach stays collapsed.
    /// </summary>
    public int MaxExpandedRows { get; set; } = 250_000;

    /// <summary>Raised when Expand all stopped at <see cref="MaxExpandedRows"/>.</summary>
    public event EventHandler? ExpandStopped;

    /// <summary>The id of the row at a point in the control (the row a context menu was opened on), or null.</summary>
    public long? IdAt(Point point)
    {
        var row = RowAt(point.Y);
        return row >= 0 ? _rows[row] : null;
    }

    /// <summary>
    /// Expands the row and everything below it. False when it stopped at <see cref="MaxExpandedRows"/>; the
    /// rows it did not reach then stay collapsed.
    /// </summary>
    public bool ExpandAll(long id)
    {
        var index = _rows.IndexOf(id);
        if (index < 0 || _model is null)
        {
            return true;
        }

        var model = _model;
        var outside = _rows.Count - (SubtreeEnd(index) - index - 1);
        var budget = Math.Max(0, MaxExpandedRows - outside);
        var complete = true;
        RebuildBranch(index, (rowId, _) =>
        {
            if (!complete || !model.HasChildren(rowId))
            {
                return null;
            }

            var children = model.Children(rowId);
            if (children.Count > budget)
            {
                complete = false;
                return null;
            }

            budget -= children.Count;
            return children;
        });
        if (!complete)
        {
            ExpandStopped?.Invoke(this, EventArgs.Empty);
        }

        return complete;
    }

    /// <summary>Collapses the row and every row below it, so that opening it again shows one level only.</summary>
    public void CollapseAll(long id)
    {
        var index = _rows.IndexOf(id);
        if (index < 0)
        {
            return;
        }

        for (int i = index, end = SubtreeEnd(index); i < end; i++)
        {
            _expanded.Remove(_rows[i]);
        }

        RebuildBranch(index, (_, _) => null);
    }

    /// <summary>
    /// One level more: the row itself when it is collapsed, otherwise every collapsed row with children at the
    /// shallowest such level below it.
    /// </summary>
    public void ExpandLevel(long id)
    {
        var index = _rows.IndexOf(id);
        if (index < 0 || _model is null)
        {
            return;
        }

        var model = _model;
        var end = SubtreeEnd(index);
        var level = int.MaxValue;
        for (var i = index; i < end; i++)
        {
            if (_depth[i] < level && !_expanded.Contains(_rows[i]) && model.HasChildren(_rows[i]))
            {
                level = _depth[i];
            }
        }

        if (level == int.MaxValue)
        {
            return;
        }

        var open = new HashSet<long>();
        for (var i = index; i < end; i++)
        {
            if (_depth[i] == level && !_expanded.Contains(_rows[i]))
            {
                open.Add(_rows[i]);
            }
        }

        RebuildBranch(index, (rowId, _) =>
            (_expanded.Contains(rowId) || open.Contains(rowId)) && model.HasChildren(rowId) ? model.Children(rowId) : null);
    }

    /// <summary>One level less: the deepest open rows of the branch close; the row itself closes last.</summary>
    public void CollapseLevel(long id)
    {
        var index = _rows.IndexOf(id);
        if (index < 0 || _model is null)
        {
            return;
        }

        var model = _model;
        var end = SubtreeEnd(index);
        var level = -1;
        for (var i = index; i < end; i++)
        {
            if (_expanded.Contains(_rows[i]))
            {
                level = Math.Max(level, _depth[i]);
            }
        }

        if (level < 0)
        {
            return;
        }

        RebuildBranch(index, (rowId, depth) =>
            depth < level && _expanded.Contains(rowId) && model.HasChildren(rowId) ? model.Children(rowId) : null);
    }

    /// <summary>+ − * / when <see cref="BranchKeys"/> is on. Read as characters, so every keyboard layout and the number pad work.</summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (!e.Handled && BranchKey(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    /// <summary>What + − * / do to the focused row's branch; false for any other key, or when <see cref="BranchKeys"/> is off.</summary>
    internal bool BranchKey(char key)
    {
        if (!BranchKeys || _focus < 0 || _focus >= _rows.Count)
        {
            return false;
        }

        var id = _rows[_focus];
        switch (key)
        {
            case '+':
                ExpandLevel(id);
                return true;
            case '-':
                CollapseLevel(id);
                return true;
            case '*':
                Cursor.Current = Cursors.WaitCursor; // a large branch takes a moment; Windows resets it afterwards
                ExpandAll(id);
                return true;
            case '/':
                CollapseAll(id);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The index after the last row below <paramref name="index"/>.</summary>
    private int SubtreeEnd(int index)
    {
        var end = index + 1;
        while (end < _rows.Count && _depth[end] > _depth[index])
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// Replaces the rows below <paramref name="index"/> with the branch as <paramref name="children"/> decides:
    /// for a row id and depth, the children to show (the row is expanded) or null (collapsed). Depth first.
    /// The row becomes the only selected one and keeps the focus.
    /// </summary>
    private void RebuildBranch(int index, Func<long, int, IReadOnlyList<long>?> children)
    {
        var rows = new List<long>();
        var depths = new List<int>();
        Add(_rows[index], _depth[index]);

        var end = SubtreeEnd(index);
        for (var i = index + 1; i < end; i++)
        {
            _selected.Remove(_rows[i]);
        }

        _rows.RemoveRange(index + 1, end - index - 1);
        _depth.RemoveRange(index + 1, end - index - 1);
        _rows.InsertRange(index + 1, rows);
        _depth.InsertRange(index + 1, depths);
        _linesDirty = true;
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - VisibleRowCount));
        SetFocus(index, extend: false, toggle: false);

        void Add(long id, int depth)
        {
            var list = children(id, depth);
            if (list is null || list.Count == 0)
            {
                _expanded.Remove(id);
                return;
            }

            _expanded.Add(id);
            foreach (var child in list)
            {
                rows.Add(child);
                depths.Add(depth + 1);
                Add(child, depth + 1);
            }
        }
    }
}
