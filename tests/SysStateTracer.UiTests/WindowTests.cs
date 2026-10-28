using System.Diagnostics;
using SysStateTracer.Controls;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>Plan Steps 41–49: the windows, checked through their controls.</summary>
public class WindowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Theme_SwitchRecoloursOpenWindows()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            ThemeManager.SetPalette(Palette.Light);
            using var form = Ui.Show(new MainForm(app.Paths, app.Settings));
            Assert.Equal(Palette.Light.Background, form.BackColor);

            ThemeManager.SetPalette(Palette.Dark);
            Ui.Pump();
            Assert.Equal(Palette.Dark.Background, form.BackColor);
            Assert.Equal(Palette.Dark.Background, form.List.BackColor);

            ThemeManager.SetPalette(Palette.Light);
            form.Close();
        });
    }

    [Fact]
    public void Main_ListsNewestFirstOrByName_AndHundredSnapshotsQuickly()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            for (var i = 0; i < 100; i++)
            {
                app.AddSnapshot($"Snapshot {i:D3}", hoursAgo: i);
            }

            using var form = Ui.Show(new MainForm(app.Paths, app.Settings));
            Assert.Equal(100, form.List.Items.Count);
            Assert.Equal("Snapshot 000", form.Snapshots[0].Name);

            // Best of three: a shared CI runner can stall one round (the target is unchanged).
            var best = long.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                var watch = Stopwatch.StartNew();
                form.Reload();
                watch.Stop();
                best = Math.Min(best, watch.ElapsedMilliseconds);
            }

            Assert.True(best < 500, $"100 snapshots listed in {best} ms (target < 500 ms)");
            output.WriteLine($"100 snapshots listed in {best} ms");

            // Sorted by clicking the column headers (F16): Name A–Z, again Z–A, then Created newest first.
            Assert.Equal(1, form.List.SortColumn);
            Assert.True(form.List.SortDescending);
            app.AddSnapshot("Aardvark", hoursAgo: 500);
            form.SortBy(0);
            Assert.Equal("Aardvark", form.Snapshots[0].Name);
            Assert.Equal(SnapshotSortOrder.Name, SettingsStore.Load(app.Paths.ConfigFile).SortOrder);
            Assert.Equal(0, form.List.SortColumn);
            Assert.False(form.List.SortDescending);
            form.SortBy(0);
            Assert.Equal("Snapshot 099", form.Snapshots[0].Name);
            Assert.Equal("Aardvark", form.Snapshots[^1].Name);
            form.SortBy(1);
            Assert.Equal("Snapshot 000", form.Snapshots[0].Name);
            Assert.Equal(SnapshotSortOrder.Date, app.Settings.SortOrder);
            Assert.True(app.Settings.SortDescending);
            form.Close();
        });
    }

    [Fact]
    public void Windows_OpenWithDefaultSizes_AndComeBackWhereTheyWereClosed()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var moved = new Rectangle(60, 40, 900, 520);
            using (var form = Ui.Show(new MainForm(app.Paths, app.Settings)))
            {
                Assert.Equal(new Size(850, 450), form.Size); // F14
                form.Bounds = moved;
                form.Close();
            }

            using (var form = Ui.Show(new MainForm(app.Paths, SettingsStore.Load(app.Paths.ConfigFile))))
            {
                Assert.Equal(moved, form.Bounds); // F12
                form.WindowState = FormWindowState.Maximized;
                Ui.Pump();
                form.Close();
            }

            var saved = SettingsStore.Load(app.Paths.ConfigFile);
            Assert.True(saved.Windows["Main"].Maximized);
            Assert.Equal(moved.Size, new Size(saved.Windows["Main"].Width, saved.Windows["Main"].Height));

            // A place on a screen that is gone: the size comes back, the position does not.
            saved.Windows["Main"] = new WindowPlacement { X = -30_000, Y = -30_000, Width = 900, Height = 520 };
            using (var form = Ui.Show(new MainForm(app.Paths, saved)))
            {
                Assert.Equal(new Size(900, 520), form.Size);
                Assert.Contains(Screen.AllScreens, s => s.WorkingArea.IntersectsWith(form.Bounds));
                form.Close();
            }

            // Every other window is at most 580 px tall when it opens (F14), and its size is remembered.
            using (var form = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
            {
                Assert.True(form.Height <= 580, $"New Snapshot is {form.Height} px tall");
                form.Size = new Size(800, 560);
                form.Close();
            }

            using (var form = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
            {
                Assert.Equal(new Size(800, 560), form.Size);
                form.Close();
            }

            using (var form = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
            {
                Assert.True(form.Height <= 580, $"Preferences is {form.Height} px tall");
                form.Close();
            }

            using (var form = Ui.Show(new AboutForm(app.Paths, app.Settings)))
            {
                Assert.True(form.Height <= 580, $"About is {form.Height} px tall");
                form.Close();
            }
        });
    }

    [Fact]
    public void Main_ShowsDamagedFilesAndTheAdministratorWarning()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var good = app.AddSnapshot("Good", hoursAgo: 1);
            File.WriteAllBytes(Path.Combine(app.SnapshotFolder, "Cut off.stsnap"), File.ReadAllBytes(good)[..^10]);

            using (var form = Ui.Show(new MainForm(app.Paths, app.Settings, elevated: false)))
            {
                Assert.Equal(2, form.List.Items.Count);
                var damaged = form.List.Items.Cast<ListViewItem>().Single(i => i.Text == "Cut off");
                Assert.Equal("Cannot be read", damaged.SubItems[1].Text);
                Assert.False(string.IsNullOrEmpty(damaged.ToolTipText));
                Assert.Contains(Controls(form), c => c is Label { Visible: true } label && label.Text.StartsWith("Not running as administrator", StringComparison.Ordinal));
                form.Close();
            }

            using (var form = Ui.Show(new MainForm(app.Paths, app.Settings, elevated: true)))
            {
                Assert.DoesNotContain(Controls(form), c => c is Label { Visible: true } label && label.Text.StartsWith("Not running as administrator", StringComparison.Ordinal));
                form.Close();
            }

            using (var properties = Ui.Show(new PropertiesForm(Path.Combine(app.SnapshotFolder, "Cut off.stsnap"))))
            {
                Assert.Contains(Controls(properties), c => c.Text.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
                properties.Close();
            }
        });
    }

    [Fact]
    public void ScopeTree_StandardUser_GreysOutWhatCannotBeRead()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var root = Directory.CreateDirectory(Path.Combine(app.Root, "scope")).FullName;
            var open = Directory.CreateDirectory(Path.Combine(root, "open")).FullName;
            var locked = Directory.CreateDirectory(Path.Combine(root, "locked"));
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory | System.Security.AccessControl.FileSystemRights.ReadData,
                System.Security.AccessControl.AccessControlType.Deny);
            var acl = locked.GetAccessControl();
            acl.AddAccessRule(rule);
            locked.SetAccessControl(acl);

            // An earlier test may have enabled SeBackupPrivilege in this process (snapshots do), which reads
            // through the deny rule. A standard user has no such privilege, so turn it off for this test.
            var hadBackup = ProcessPrivileges.TryDisable(ProcessPrivileges.Backup);
            try
            {
                using var form = new Form();
                var tree = new ScopeTree { Dock = DockStyle.Fill, Elevated = false };
                form.Controls.Add(tree);
                Ui.Show(form);
                tree.Load(new SnapshotScope { FileSystemRoots = [root], RegistryRoots = [], IncludeServices = false }, []);

                var folder = Find(tree.Nodes, root);
                folder.Expand();
                var lockedNode = Find(folder.Nodes, locked.FullName);
                Assert.True(((ScopeItem)lockedNode.Tag!).NoAccess);
                Assert.Contains("no access", lockedNode.Text, StringComparison.Ordinal);
                Assert.Equal(ScopeTree.Unchecked, ScopeTree.StateOf(lockedNode));
                Assert.Equal(ScopeTree.Checked, ScopeTree.StateOf(Find(folder.Nodes, open)));
                Assert.Equal(ScopeTree.Checked, ScopeTree.StateOf(folder));

                tree.Toggle(lockedNode);
                Assert.Equal(ScopeTree.Unchecked, ScopeTree.StateOf(lockedNode));
                Assert.Empty(tree.ToScope().Excluded);
                form.Close();
            }
            finally
            {
                if (hadBackup)
                {
                    ProcessPrivileges.TryEnable(ProcessPrivileges.Backup);
                }

                acl.RemoveAccessRule(rule);
                locked.SetAccessControl(acl);
            }
        });
    }

    [Fact]
    public void ScopeTree_RoundTripsTheSavedScope()
    {
        Ui.Run(() =>
        {
            using var form = new Form();
            var tree = new ScopeTree { Dock = DockStyle.Fill };
            form.Controls.Add(tree);
            Ui.Show(form);
            var volumes = VolumeDiscovery.GetFixedVolumes();
            var system = volumes.First(v => Environment.SystemDirectory.StartsWith(v.RootPath, StringComparison.OrdinalIgnoreCase));

            // Default scope: every fixed drive, HKLM, HKU, services.
            tree.Load(new SnapshotScope(), volumes);
            var scope = tree.ToScope();
            Assert.Null(scope.FileSystemRoots);
            Assert.Equal(["HKLM", "HKU"], scope.RegistryRoots);
            Assert.True(scope.IncludeServices);
            Assert.False(scope.IncludeDrivers);
            Assert.Empty(scope.Excluded);

            // Ignored items are unchecked and labelled; they are not "exclusions".
            tree.SetIgnore(GlobalIgnoreList.Build([@"REG:HKLM\SAM"]).Matcher);
            var hklm = Find(tree.Nodes, "HKLM");
            hklm.Expand();
            var sam = Find(hklm.Nodes, @"HKLM\SAM");
            Assert.Equal(ScopeTree.Unchecked, ScopeTree.StateOf(sam));
            Assert.EndsWith("(ignored)", sam.Text, StringComparison.Ordinal);
            Assert.Equal(ScopeTree.Checked, ScopeTree.StateOf(hklm));
            Assert.Empty(tree.ToScope().Excluded);

            // Unchecking a folder inside an included drive records it.
            var drive = Find(tree.Nodes, system.RootPath);
            drive.Expand();
            var windows = Find(drive.Nodes, Path.GetDirectoryName(Environment.SystemDirectory)!);
            tree.Toggle(windows);
            Assert.Equal(ScopeTree.Mixed, ScopeTree.StateOf(drive));
            scope = tree.ToScope();
            Assert.Contains(IgnoreRuleParser.Format(IgnoreTarget.FileSystem, windows.Name), scope.Excluded);

            // Unchecking a hive and the drivers.
            tree.Toggle(Find(tree.Nodes, "HKU"));
            scope = tree.ToScope();
            Assert.Equal(["HKLM"], scope.RegistryRoots);

            // The same scope loads back with the same states.
            tree.Load(scope, volumes);
            Assert.Equal(ScopeTree.Mixed, ScopeTree.StateOf(Find(tree.Nodes, system.RootPath)));
            Assert.Equal(ScopeTree.Unchecked, ScopeTree.StateOf(Find(tree.Nodes, "HKU")));
            var again = tree.ToScope();
            Assert.Equal(scope.Excluded, again.Excluded);
            Assert.Equal(scope.RegistryRoots, again.RegistryRoots);
            form.Close();
        });
    }

    [Fact]
    public void NewSnapshot_StartTakesTheSnapshot_StopSavesNothing()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var data = Directory.CreateDirectory(Path.Combine(app.Root, "data")).FullName;
            for (var i = 0; i < 20; i++)
            {
                File.WriteAllText(Path.Combine(data, $"file{i}.txt"), "x");
            }

            File.WriteAllText(Path.Combine(app.DataFolder, AppPaths.LocalIgnoreFileName), "# none" + Environment.NewLine);
            app.Settings.Scope = new SnapshotScope { FileSystemRoots = [data], RegistryRoots = [], IncludeServices = false };

            using (var form = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
            {
                var start = Controls(form).OfType<ThemedButton>().Single(b => b.Text == "Start");
                form.NameBox.Text = "Small folder";
                Assert.True(Ui.PumpUntil(() => start.Enabled, 10_000), "Start stayed disabled");
                start.PerformClick();
                Assert.True(Ui.PumpUntil(() => form.CreatedPath is not null && !form.IsRunning, 60_000), "the snapshot did not finish");
                Assert.True(File.Exists(form.CreatedPath));
                Assert.False(start.Enabled);
                form.Close();
            }

            // A larger scope, stopped right away: nothing is saved and the window can close.
            app.Settings.Scope = new SnapshotScope { FileSystemRoots = [Path.GetDirectoryName(Environment.SystemDirectory)!], RegistryRoots = [], IncludeServices = false };
            var before = Directory.GetFiles(app.SnapshotFolder).Length;
            using (var form = Ui.Show(new NewSnapshotForm(app.Paths, app.Settings)))
            {
                var buttons = Controls(form).OfType<ThemedButton>().ToList();
                var start = buttons.Single(b => b.Text == "Start");
                var stop = buttons.Single(b => b.Text == "Stop");
                form.NameBox.Text = "Stopped run";   // the default name could equal the first one, taken within the same second
                Assert.True(Ui.PumpUntil(() => start.Enabled, 10_000), "Start stayed disabled");
                start.PerformClick();
                Assert.True(Ui.PumpUntil(() => stop.Enabled, 5_000));
                stop.PerformClick();
                Assert.True(Ui.PumpUntil(() => !form.IsRunning, 60_000), "Stop did not end the run");
                Assert.Null(form.CreatedPath);
                Assert.Equal(before, Directory.GetFiles(app.SnapshotFolder).Length);
                form.Close();
            }
        });
    }

    [Fact]
    public void Properties_ShowsEveryGroupAndSkippedItems()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var path = app.AddSnapshot("Before Office 2024 install", hoursAgo: 2, withSkipped: true);
            using var form = Ui.Show(new PropertiesForm(path));
            var texts = Controls(form).Select(c => c.Text).ToList();
            foreach (var expected in new[] { "General", "Timing", "Scope", "Counts", "Before Office 2024 install", path, "Windows 11 Pro (10.0.26100.6584)" })
            {
                Assert.Contains(expected, texts);
            }

            Assert.Contains(texts, t => t.StartsWith("Show skipped items (3)", StringComparison.Ordinal));
            form.Close();
        });
    }

    [Fact]
    public void About_ShowsVersionLinkAndCommandLine()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            using var form = Ui.Show(new AboutForm(app.Paths, app.Settings));
            var all = Controls(form).ToList();
            Assert.Contains(all, c => c is LinkLabel link && link.Text == Core.AppInfo.GitHubUrl);
            Assert.Contains(all, c => c is TextBox box && box.Text.Contains("/compare-snap", StringComparison.Ordinal));
            form.Close();
        });
    }

    [Fact]
    public void Preferences_SavesFolderThemeAndIgnoreChoice()
    {
        Ui.Run(() =>
        {
            using var app = new TestApp();
            var other = Directory.CreateDirectory(Path.Combine(app.Root, "other snapshots")).FullName;
            using (var form = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
            {
                var all = Controls(form).ToList();
                all.OfType<TextBox>().First(t => t.Text == app.SnapshotFolder).Text = other;
                all.OfType<RadioButton>().Single(r => r.Text == "Dark").Checked = true;
                Assert.True(ThemeManager.Current.IsDark);
                all.OfType<CheckBox>().Single(r => r.Text.StartsWith("Also use the shared remote list", StringComparison.Ordinal)).Checked = true;

                // Grid lines and zebra striping show at once in open windows.
                form.GridLinesBox.Checked = true;
                form.ZebraBox.Checked = true;
                Assert.True(ThemeManager.GridLines);
                Assert.True(ThemeManager.ZebraStripes);

                // A rule is edited in place, e.g. to add a wildcard (F25).
                var rules = all.OfType<ThemedListView>().Single();
                var sam = rules.Items.Cast<ListViewItem>().Single(i => i.SubItems[1].Text == @"REG:HKLM\SAM");
                form.ReplaceRule(int.Parse(sam.Name, System.Globalization.CultureInfo.InvariantCulture), [@"REG:HKLM\SAM*"]);
                Assert.Contains(rules.Items.Cast<ListViewItem>(), i => i.SubItems[1].Text == @"REG:HKLM\SAM*");
                all.OfType<ThemedButton>().Single(b => b.Text == "OK").PerformClick();
                form.Close();
                Ui.Pump();
            }

            var saved = SettingsStore.Load(app.Paths.ConfigFile);
            Assert.Equal(other, saved.SnapshotFolder);
            Assert.Equal(ThemePreference.Dark, saved.Theme);
            Assert.Equal(IgnoreListSource.Remote, saved.IgnoreListSource);
            Assert.True(saved.ShowGridLines);
            Assert.True(saved.ZebraStripes);
            Assert.Contains(@"REG:HKLM\SAM*", File.ReadAllText(app.Paths.LocalIgnoreFile), StringComparison.Ordinal);

            // The rules dialog accepts only valid rules (F20).
            using (var dialog = Ui.Show(IgnoreRulesDialog.Create("Add to Global Ignore list", "Add?", [@"FS:C:\Temp"], "Add")))
            {
                Assert.True(dialog.CanSave);
                dialog.Box.Text = "FS:C:\\Users\\*\\AppData\r\nnot a rule";
                Assert.False(dialog.CanSave);
                dialog.Box.Text = "FS:C:\\Users\\*\\AppData\r\nREG:HKCU\\Software\\X";
                Assert.True(dialog.CanSave);
                Assert.Equal(2, dialog.Rules.Count);
                dialog.Close();
            }

            // Cancel puts the theme back.
            using (var form = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
            {
                Controls(form).OfType<RadioButton>().Single(r => r.Text == "Light").Checked = true;
                Assert.False(ThemeManager.Current.IsDark);
                form.ZebraBox.Checked = false;
                Assert.False(ThemeManager.ZebraStripes);

                // Only one theme is chosen at a time, whether its picture or its button is clicked (F13).
                var radios = Controls(form).OfType<RadioButton>().Where(r => r.Text is "Follow Windows" or "Light" or "Dark").ToList();
                var pictures = Controls(form).OfType<ThemePreview>().ToList();
                pictures.Single(p => p.Theme == ThemePreference.System).PerformClick();
                pictures.Single(p => p.Theme == ThemePreference.Dark).PerformClick();
                Assert.Equal("Dark", Assert.Single(radios, r => r.Checked).Text);
                Assert.Equal(ThemePreference.Dark, Assert.Single(pictures, p => p.Selected).Theme);
                radios.Single(r => r.Text == "Light").Checked = true;
                Assert.Equal("Light", Assert.Single(radios, r => r.Checked).Text);
                Assert.False(ThemeManager.Current.IsDark);
                Controls(form).OfType<ThemedButton>().Single(b => b.Text == "Cancel").PerformClick();
                form.Close();
                Ui.Pump();
            }

            Assert.True(ThemeManager.Current.IsDark);
            Assert.True(ThemeManager.ZebraStripes); // Cancel put it back too
            ThemeManager.SetPreference(ThemePreference.Light);
            ThemeManager.SetListStyle(false, false);
        });
    }

    internal static IEnumerable<Control> Controls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Controls(child))
            {
                yield return nested;
            }
        }
    }

    private static TreeNode Find(TreeNodeCollection nodes, string path)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is ScopeItem item && string.Equals(item.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.Tag is ScopeItem { Kind: ScopeKind.Category })
            {
                var found = FindOrNull(node.Nodes, path);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        throw new InvalidOperationException($"No node for {path}");
    }

    private static TreeNode? FindOrNull(TreeNodeCollection nodes, string path)
    {
        try
        {
            return Find(nodes, path);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
