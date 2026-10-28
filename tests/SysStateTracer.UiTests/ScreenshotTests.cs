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
                    main.List.DropTargetIndex = -1;
                    ThemeManager.SetListStyle(true, true);
                    Ui.Pump();
                    Ui.Screenshot(main, "w1-main-grid-zebra");
                    ThemeManager.SetListStyle(false, false);
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

                    // Show the top of the tree, with a new folder selected (its path is in the status bar).
                    var office = Enumerable.Range(0, comparison.Diff.FileSystem.Count)
                        .FirstOrDefault(n => comparison.Diff.Name(comparison.Diff.FileSystem, n) == "Microsoft Office", -1);
                    grid.Select(office >= 0 ? ComparisonModel.NodeId(Core.Comparison.DiffArea.FileSystem, office) : model.Roots[0]);
                    grid.ScrollTo(0);
                    grid.Focus();
                    Ui.Screenshot(comparison, "w4-comparison");
                    ThemeManager.SetListStyle(true, true);
                    Ui.Pump();
                    Ui.Screenshot(comparison, "w4-comparison-grid-zebra");
                    ThemeManager.SetListStyle(false, false);
                    comparison.Swap();
                    Ui.Screenshot(comparison, "w4-comparison-swapped");
                    comparison.Close();
                }

                using (var about = Ui.Show(new AboutForm(app.Paths, app.Settings)))
                {
                    Ui.Screenshot(about, "w6-about");
                    about.Close();
                }

                // A snapshot being taken (a large folder, stopped after the screenshot).
                var savedScope = app.Settings.Scope;
                app.Settings.Scope = new SnapshotScope { FileSystemRoots = [Path.GetDirectoryName(Environment.SystemDirectory)!], RegistryRoots = ["HKLM"], IncludeServices = true };
                using (var running = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
                {
                    var buttons = WindowTests.Controls(running).OfType<ThemedButton>().ToList();
                    var start = buttons.Single(b => b.Text == "Start");
                    running.NameBox.Text = "Before printer driver update";
                    Ui.PumpUntil(() => start.Enabled, 10_000);
                    start.PerformClick();
                    Ui.Pump(3500);
                    Ui.Screenshot(running, "w3-new-snapshot-running");
                    buttons.Single(b => b.Text == "Stop").PerformClick();
                    Assert.True(Ui.PumpUntil(() => !running.IsRunning, 180_000), "The snapshot did not stop within 3 minutes.");
                    running.Close();
                }

                app.Settings.Scope = savedScope;

                // Dialogs shown in the manual.
                using (var question = Ui.Show(MessageDialog.Question(
                    "Revert Value",
                    "Do you want to revert to value from Snapshot Before Office 2024 install? A backup of the current values is saved in C:\\Tools\\SysStateTracer\\Backups first.",
                    "Revert",
                    "Cancel",
                    [@"HKLM\SOFTWARE\Contoso\Version:  REG_SZ 2.4  →  REG_SZ 1.0", @"HKLM\SOFTWARE\Contoso\Flags:  REG_DWORD 0x00000007 (7)  →  REG_DWORD 0x00000003 (3)", @"HKLM\SOFTWARE\Contoso\LegacyMode:  (does not exist)  →  REG_DWORD 0x00000001 (1)"])))
                {
                    Ui.Screenshot(question, "dialog-revert");
                    question.Close();
                }

                using (var rules = Ui.Show(IgnoreRulesDialog.Create(
                    "Add to Global Ignore list",
                    "Add these 2 rules to the local Global Ignore list? Change them first if needed. They are left out of new snapshots and comparisons from now on, and hidden here now.",
                    [@"FS:C:\Users\*\AppData\Local\Contoso\Cache", @"REG:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs"],
                    "Add")))
                {
                    Ui.Screenshot(rules, "dialog-add-to-ignore");
                    rules.Close();
                }

                using (var input = Ui.Show(InputDialog.Create("Add registry key", "Key path, e.g. HKLM\\SOFTWARE\\Vendor\\App\\Cache or HKCU\\Software\\App\\MRU:", @"HKCU\Software\Contoso\RecentFiles", "Add", _ => null)))
                {
                    Ui.Screenshot(input, "dialog-add-registry-key");
                    input.Close();
                }

                using (var picker = Ui.Show(new ServicePickerDialog([("BITS", "Background Intelligent Transfer Service"), ("edgeupdate", "Microsoft Edge Update Service (edgeupdate)"), ("ClickToRunSvc", "Microsoft Office Click-to-Run Service"), ("Spooler", "Print Spooler"), ("wuauserv", "Windows Update")])))
                {
                    Ui.Screenshot(picker, "dialog-add-service");
                    picker.Close();
                }

                // Copy to .\Files, finished.
                var sources = Directory.CreateDirectory(Path.Combine(app.Root, "copy-source")).FullName;
                var toCopy = new List<Core.Export.FileToCopy>();
                for (var i = 0; i < 6; i++)
                {
                    var file = Path.Combine(sources, $"office{i:D2}.dll");
                    File.WriteAllBytes(file, new byte[200_000 + (i * 1000)]);
                    toCopy.Add(new Core.Export.FileToCopy(file, $@"Program Files\Microsoft Office\root\office{i:D2}.dll", 200_000 + (i * 1000)));
                }

                toCopy.Add(new Core.Export.FileToCopy(Path.Combine(sources, "in-use.log"), @"Temp\in-use.log", 10));
                using (var copy = Ui.Show(new CopyProgressForm(toCopy, Path.Combine(app.DataFolder, "Files"))))
                {
                    Ui.PumpUntil(() => copy.Report is not null, 30_000);
                    Ui.Pump(300);
                    Ui.Screenshot(copy, "dialog-copy-to-files");
                    copy.Close();
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
