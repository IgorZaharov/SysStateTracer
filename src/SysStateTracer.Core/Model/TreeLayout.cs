namespace SysStateTracer.Core.Model;

/// <summary>
/// Turns entries added in any order (each with a parent index) into the snapshot layout:
/// depth-first pre-order, siblings sorted by <see cref="NameTable.Compare"/>, with descendant counts.
/// In that layout the subtree of entry i is exactly entries i+1 .. i+Descendants[i].
/// </summary>
internal static class TreeLayout
{
    internal readonly record struct Result(int[] Order, int[] Parent, int[] Descendants);

    /// <param name="count">Number of entries.</param>
    /// <param name="parent">Parent index per entry; -1 for a root. Parents must be valid indices.</param>
    /// <param name="nameId">Name id per entry.</param>
    /// <param name="names">Name table the ids refer to.</param>
    /// <returns>
    /// Order[newIndex] = old index; Parent and Descendants in the new layout.
    /// </returns>
    public static Result PreOrder(int count, int[] parent, int[] nameId, NameTable names)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(nameId);
        ArgumentNullException.ThrowIfNull(names);

        // Children grouped per parent (CSR layout). Group 0 = roots, group p+1 = children of p.
        var groupStart = new int[count + 2];
        for (var i = 0; i < count; i++)
        {
            var p = parent[i];
            if (p < -1 || p >= count || p == i)
            {
                throw new InvalidOperationException($"Entry {i} has an invalid parent {p}.");
            }

            groupStart[p + 2]++;
        }

        for (var g = 1; g < groupStart.Length; g++)
        {
            groupStart[g] += groupStart[g - 1];
        }

        var children = new int[count];
        var fill = (int[])groupStart.Clone();
        for (var i = 0; i < count; i++)
        {
            children[fill[parent[i] + 1]++] = i;
        }

        var comparer = new SiblingComparer(nameId, names);
        for (var g = 0; g < count + 1; g++)
        {
            var length = groupStart[g + 1] - groupStart[g];
            if (length > 1)
            {
                Array.Sort(children, groupStart[g], length, comparer);
            }
        }

        // Iterative depth-first walk; children pushed in reverse so the first sibling comes out first.
        var order = new int[count];
        var newIndex = new int[count];
        var stack = new int[count];
        var top = 0;
        for (var c = groupStart[1] - 1; c >= groupStart[0]; c--)
        {
            stack[top++] = children[c];
        }

        var written = 0;
        while (top > 0)
        {
            var old = stack[--top];
            newIndex[old] = written;
            order[written++] = old;
            for (var c = groupStart[old + 2] - 1; c >= groupStart[old + 1]; c--)
            {
                stack[top++] = children[c];
            }
        }

        if (written != count)
        {
            throw new InvalidOperationException("The entries do not form a tree (a parent chain loops).");
        }

        var newParent = new int[count];
        for (var i = 0; i < count; i++)
        {
            var p = parent[order[i]];
            newParent[i] = p < 0 ? -1 : newIndex[p];
        }

        var descendants = new int[count];
        for (var i = count - 1; i >= 0; i--)
        {
            var p = newParent[i];
            if (p >= 0)
            {
                descendants[p] += descendants[i] + 1;
            }
        }

        return new Result(order, newParent, descendants);
    }

    private sealed class SiblingComparer(int[] nameId, NameTable names) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var result = NameTable.Compare(names[nameId[x]], names[nameId[y]]);
            return result != 0 ? result : x.CompareTo(y);
        }
    }
}
