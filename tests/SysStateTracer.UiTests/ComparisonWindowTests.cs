using System.Diagnostics;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>Plan Steps 50–52: the tree grid and the comparison window.</summary>
public class ComparisonWindowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Grid_MillionRows_ExpandAndPaintWithinAFrame()
    {
        Ui.Run(() =>
        {
            using var form = new Form { ClientSize = new Size(1000, 900) };
            var grid = new VirtualTreeGrid { Dock = DockStyle.Fill };
            grid.Columns.Add(new GridColumn("Name", 400));
            grid.Columns.Add(new GridColumn(string.Empty, 28));
            grid.Columns.Add(new GridColumn("Snapshot 1", 250));
            grid.Columns.Add(new GridColumn("Snapshot 2", 250));
            form.Controls.Add(grid);
            Ui.Show(form);
            grid.Model = new FlatModel(1_000_000);

            var expand = Stopwatch.StartNew();
            grid.Expand(-1);
            expand.Stop();
            Assert.Equal(1_000_001, grid.RowCount);

            // A frame = scroll and paint on screen (Refresh paints synchronously).
            grid.Refresh();
            grid.Refresh(); // warm-up
            var frames = new List<double>();
            foreach (var row in new[] { 0, 500_000, 999_000, 250_000, 750_123, 10, 600_000, 333_333 })
            {
                var frame = Stopwatch.StartNew();
                grid.ScrollTo(row);
                grid.Refresh();
                frames.Add(frame.Elapsed.TotalMilliseconds);
            }

            frames.Sort();
            var median = frames[frames.Count / 2];
            output.WriteLine($"1,000,000 rows: expand {expand.ElapsedMilliseconds} ms; scroll + paint median {median:0.0} ms, slowest {frames[^1]:0.0} ms (target < 16 ms)");
            Assert.True(median < 16, $"median frame {median:0.0} ms");
            Assert.True(expand.ElapsedMilliseconds < 1000, $"expand took {expand.ElapsedMilliseconds} ms");

            // Ctrl+A on a million rows, then collapse.
            var select = Stopwatch.StartNew();
            grid.SelectAll();
            select.Stop();
            Assert.Equal(1_000_001, grid.SelectedIds.Count);
            grid.Collapse(-1);
            Assert.Equal(1, grid.RowCount);
            output.WriteLine($"Select all: {select.ElapsedMilliseconds} ms");
            form.Close();
        });
    }

    [Fact]
    public void Comparison_ShowsChanges_Swaps_AndAppliesMenuRules()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var before = app.AddSnapshot("Before Office 2024 install", hoursAgo: 30, extraFiles: 3);
            var after = app.AddSnapshot("After Office 2024 install", hoursAgo: 29, extraFiles: 8, installed: true);

            // Given newer-first, the older snapshot still goes on the left.
            using var form = new ComparisonForm(app.Paths, app.Settings, after, before);
            Ui.Show(form);
            Assert.True(Ui.PumpUntil(() => form.Diff is not null && !form.IsLoading, 30_000), "comparison did not load");
            var diff = form.Diff!;
            Assert.Equal("Before Office 2024 install", diff.Left.Meta.Name);
            Assert.True(diff.FileSystemCounts.New > 0);
            Assert.True(diff.RegistryCounts.Modified > 0);
            Assert.Equal(3, form.Grid.RowCount); // the three categories, collapsed

            var model = (ComparisonModel)form.Grid.Model!;
            Assert.StartsWith("File system  (", model.Text(ComparisonModel.CategoryId(DiffArea.FileSystem), 0), StringComparison.Ordinal);

            // Expand the registry down to the changed value.
            form.Grid.Expand(ComparisonModel.CategoryId(DiffArea.Registry));
            var registry = diff.Registry;
            var version = Enumerable.Range(0, registry.Count).Single(n => diff.FullPath(registry, n) == @"HKLM\SOFTWARE\Contoso\Version");
            var versionId = ComparisonModel.NodeId(DiffArea.Registry, version);
            var ancestors = new List<int>();
            for (var n = registry.Parent(version); n >= 0; n = registry.Parent(n))
            {
                ancestors.Insert(0, n);
            }

            foreach (var n in ancestors)
            {
                form.Grid.Expand(ComparisonModel.NodeId(DiffArea.Registry, n));
            }

            Assert.True(form.Grid.RowCount > 3 + ancestors.Count);
            Assert.Equal("~", model.Text(versionId, ComparisonModel.StatusColumn));
            Assert.Contains("1.0", model.Text(versionId, ComparisonModel.LeftColumn), StringComparison.Ordinal);
            Assert.Contains("2.4", model.Text(versionId, ComparisonModel.RightColumn), StringComparison.Ordinal);
            Assert.Equal(ThemeManager.Current.Modified, model.Style(versionId).TextColor);

            // Swap: columns trade places, New and Deleted swap colours; the expanded rows stay.
            var rows = form.Grid.RowCount;
            form.Swap();
            model = (ComparisonModel)form.Grid.Model!;
            Assert.Equal("After Office 2024 install", form.Diff!.Left.Meta.Name);
            Assert.Contains("2.4", model.Text(versionId, ComparisonModel.LeftColumn), StringComparison.Ordinal);
            Assert.Equal(rows, form.Grid.RowCount);
            form.Swap();

            // Menu rules (§10.6).
            var fsCategory = DiffActions.Category(DiffArea.FileSystem);
            var value = new DiffItem(DiffArea.Registry, version);
            Assert.True(DiffActions.IsEnabled(DiffAction.JumpTo, [value]));
            Assert.True(DiffActions.IsEnabled(DiffAction.RevertValue, [value]));
            Assert.False(DiffActions.IsEnabled(DiffAction.CopyToFiles, [value]));
            Assert.True(DiffActions.IsEnabled(DiffAction.CopyToFiles, [fsCategory]));
            Assert.False(DiffActions.IsEnabled(DiffAction.JumpTo, [fsCategory]));

            // Marks strike the row and everything below it, until the window closes.
            form.Grid.Select(versionId);
            form.Marks.Toggle([value]);
            Assert.True(((ComparisonModel)form.Grid.Model!).Style(versionId).Strikeout);
            form.Close();
        });
    }

    /// <summary>One root with <c>count</c> children; text made on demand like the comparison model.</summary>
    private sealed class FlatModel(int count) : ITreeGridModel
    {
        private readonly long[] _children = [.. Enumerable.Range(0, count).Select(i => (long)i)];

        public IReadOnlyList<long> Roots { get; } = [-1];

        public bool HasChildren(long id) => id == -1;

        public IReadOnlyList<long> Children(long id) => id == -1 ? _children : [];

        public string Text(long id, int column) => column switch
        {
            0 => id < 0 ? "Root" : $"file{id:D7}.dll",
            1 => "+",
            2 => "2026-10-27 14:05:12 · 812 KB",
            _ => "2026-10-28 09:00:00 · 1.2 MB",
        };

        public string? ToolTip(long id, int column) => null;

        public GridRowStyle Style(long id) => new(id % 3 == 0 ? Color.Green : null, id % 7 == 0, id % 7 == 0, id < 0);
    }
}
