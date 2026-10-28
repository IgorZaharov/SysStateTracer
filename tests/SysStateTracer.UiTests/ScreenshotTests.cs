using SysStateTracer.Controls;
using SysStateTracer.Core.Config;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>
/// Saves every window in the light and the dark theme when SYSSTATETRACER_SCREENSHOTS is set
/// (CI publishes them on the "ui-preview" pre-release), so the look can be reviewed without a Windows PC.
/// </summary>
public class ScreenshotTests
{
    [Fact]
    public void AllWindows_BothThemes()
    {
        if (Ui.ScreenshotFolder is null)
        {
            return;
        }

        Ui.Run(() =>
        {
            using var app = new TestApp();
            var before = app.AddSnapshot("Before Office 2024 install", hoursAgo: 30, extraFiles: 3);
            var after = app.AddSnapshot("After Office 2024 install", hoursAgo: 29, extraFiles: 8, withSkipped: true, installed: true);
            app.AddSnapshot("Monthly baseline October", hoursAgo: 260);
            app.AddSnapshot("20261027_14-30-00_OFFICE-PC", hoursAgo: 20);
            app.AddSnapshot("Build server before agent update", hoursAgo: 120, computer: "BUILD-07");

            foreach (var palette in new[] { Palette.Light, Palette.Dark })
            {
                ThemeManager.SetPalette(palette);

                using (var main = Ui.Show(new MainForm(app.Paths, app.Settings)))
                {
                    main.List.Items[1].Selected = true;
                    main.List.Focus();
                    Ui.Screenshot(main, "w1-main");
                    main.List.DropTargetIndex = 3;
                    Ui.Screenshot(main, "w1-main-drop-target");
                    main.Close();
                }

                using (var properties = Ui.Show(new PropertiesForm(after)))
                {
                    Ui.Screenshot(properties, "w2-properties");
                    properties.Close();
                }

                using (var newSnapshot = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
                {
                    Ui.Pump(1500); // the Global Ignore list labels "(ignored)" items
                    Ui.Screenshot(newSnapshot, "w3-new-snapshot");
                    newSnapshot.Close();
                }

                using (var preferences = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
                {
                    Ui.Screenshot(preferences, "w5-preferences-general");
                    preferences.ShowIgnorePage();
                    Ui.Screenshot(preferences, "w5-preferences-ignore-list");
                    preferences.Close();
                }

                using (var comparison = Ui.Show(new ComparisonForm(app.Paths, app.Settings, before, after)))
                {
                    Ui.PumpUntil(() => comparison.Diff is not null && !comparison.IsLoading, 30_000);
                    var grid = comparison.Grid;
                    foreach (var area in new[] { Core.Comparison.DiffArea.FileSystem, Core.Comparison.DiffArea.Registry, Core.Comparison.DiffArea.Services })
                    {
                        ExpandAll(grid, ComparisonModel.CategoryId(area));
                    }

                    var model = (ComparisonModel)grid.Model!;
                    var temp = Enumerable.Range(0, comparison.Diff!.FileSystem.Count)
                        .FirstOrDefault(n => comparison.Diff.Name(comparison.Diff.FileSystem, n) == "Temp", -1);
                    if (temp >= 0)
                    {
                        comparison.Marks.Toggle([new Core.Comparison.DiffItem(Core.Comparison.DiffArea.FileSystem, temp)]);
                    }

                    grid.Select(model.Roots[1]);
                    grid.Focus();
                    Ui.Screenshot(comparison, "w4-comparison");
                    comparison.Swap();
                    Ui.Screenshot(comparison, "w4-comparison-swapped");
                    comparison.Close();
                }

                using (var about = Ui.Show(new AboutForm(app.Paths, app.Settings)))
                {
                    Ui.Screenshot(about, "w6-about");
                    about.Close();
                }

                using (var sidebar = Ui.Show(new SidebarSample()))
                {
                    Ui.Screenshot(sidebar, "controls");
                    sidebar.Close();
                }
            }

            ThemeManager.SetPalette(Palette.Light);
        });
    }

    private static void ExpandAll(VirtualTreeGrid grid, long id)
    {
        grid.Expand(id);
        foreach (var child in grid.Model!.Children(id))
        {
            if (grid.Model.HasChildren(child) && grid.RowCount < 60)
            {
                ExpandAll(grid, child);
            }
        }
    }

    /// <summary>Shared controls in their states: buttons, sidebar icons, progress, menu.</summary>
    private sealed class SidebarSample : ThemedDialog
    {
        public SidebarSample()
            : base("Controls")
        {
            ClientSize = new Size(520, 220);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            flow.Controls.Add(new SideBarButton(Glyphs.Add, "New"));
            flow.Controls.Add(new SideBarButton(Glyphs.Delete, "Delete") { Enabled = false });
            flow.Controls.Add(new SideBarButton(Glyphs.Settings, "Preferences"));
            flow.Controls.Add(new ThemedButton { Text = "Primary", Primary = true });
            flow.Controls.Add(new ThemedButton { Text = "Secondary" });
            flow.Controls.Add(new ThemedButton { Text = "Disabled", Enabled = false });
            flow.Controls.Add(new ThemedProgressBar { Width = 460, Height = 8, Value = 0.6 });
            flow.Controls.Add(new CheckBox { Text = "A check box", AutoSize = true, Checked = true });
            flow.Controls.Add(new RadioButton { Text = "A radio button", AutoSize = true, Checked = true });
            flow.Controls.Add(new TextBox { Text = "A text box", Width = 200, BorderStyle = BorderStyle.FixedSingle });
            Controls.Add(flow);
            flow.BringToFront();
            AddButton("Close", DialogResult.Cancel, primary: true);
        }
    }
}
