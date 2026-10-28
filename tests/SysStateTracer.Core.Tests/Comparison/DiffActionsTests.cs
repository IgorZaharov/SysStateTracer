using SysStateTracer.Core.Comparison;

namespace SysStateTracer.Core.Tests.Comparison;

/// <summary>Plan §10.6 and F3: which context-menu actions a selection allows.</summary>
public class DiffActionsTests
{
    private static readonly DiffItem Folder = new(DiffArea.FileSystem, 3);
    private static readonly DiffItem File2 = new(DiffArea.FileSystem, 4);
    private static readonly DiffItem Key = new(DiffArea.Registry, 1);
    private static readonly DiffItem Value = new(DiffArea.Registry, 2);
    private static readonly DiffItem Service = new(DiffArea.Services, 0);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rules(DiffAction action, DiffItem[] selection, bool enabled)
    {
        Assert.Equal(enabled, DiffActions.IsEnabled(action, selection));
    }

    public static TheoryData<DiffAction, DiffItem[], bool> Cases() => new()
    {
        // Jump to: one item, never a category or several.
        { DiffAction.JumpTo, [Folder], true },
        { DiffAction.JumpTo, [Value], true },
        { DiffAction.JumpTo, [Service], true },
        { DiffAction.JumpTo, [Folder, File2], false },
        { DiffAction.JumpTo, [DiffActions.Category(DiffArea.Registry)], false },
        { DiffAction.JumpTo, [], false },

        // Mark and Add to Global Ignore list: any items, several too.
        { DiffAction.MarkNonImportant, [Folder, Key, Service], true },
        { DiffAction.AddToIgnoreList, [Folder, Key, Service], true },
        { DiffAction.AddToIgnoreList, [DiffActions.Category(DiffArea.Services)], false },

        // Export to .REG: registry only; a whole category or hive too (F3).
        { DiffAction.ExportReg, [Key, Value], true },
        { DiffAction.ExportReg, [DiffActions.Category(DiffArea.Registry)], true },
        { DiffAction.ExportReg, [Key, Folder], false },
        { DiffAction.ExportReg, [Service], false },

        // Copy to .\Files: files and folders only; the whole file-system category too (F3).
        { DiffAction.CopyToFiles, [Folder, File2], true },
        { DiffAction.CopyToFiles, [DiffActions.Category(DiffArea.FileSystem)], true },
        { DiffAction.CopyToFiles, [Folder, Value], false },

        // Revert Value: registry keys and values, never a whole category.
        { DiffAction.RevertValue, [Key, Value], true },
        { DiffAction.RevertValue, [DiffActions.Category(DiffArea.Registry)], false },
        { DiffAction.RevertValue, [Value, Service], false },
    };
}
