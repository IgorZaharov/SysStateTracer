using System.Diagnostics;
using SysStateTracer.Controls;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
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

            var watch = Stopwatch.StartNew();
            form.Reload();
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 500, $"100 snapshots listed in {watch.ElapsedMilliseconds} ms (target < 500 ms)");
            output.WriteLine($"100 snapshots listed in {watch.ElapsedMilliseconds} ms");

            app.Settings.SortOrder = SnapshotSortOrder.Name;
            app.AddSnapshot("Aardvark", hoursAgo: 500);
            form.Reload();
            Assert.Equal("Aardvark", form.Snapshots[0].Name);
            form.Close();
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
                all.OfType<RadioButton>().Single(r => r.Text.StartsWith("Remote", StringComparison.Ordinal)).Checked = true;
                all.OfType<ThemedButton>().Single(b => b.Text == "OK").PerformClick();
                form.Close();
                Ui.Pump();
            }

            var saved = SettingsStore.Load(app.Paths.ConfigFile);
            Assert.Equal(other, saved.SnapshotFolder);
            Assert.Equal(ThemePreference.Dark, saved.Theme);
            Assert.Equal(IgnoreListSource.Remote, saved.IgnoreListSource);
            Assert.Contains(@"REG:HKLM\SAM", File.ReadAllText(app.Paths.LocalIgnoreFile), StringComparison.Ordinal);

            // Cancel puts the theme back.
            using (var form = Ui.Show(new PreferencesForm(app.Paths, app.Settings)))
            {
                Controls(form).OfType<RadioButton>().Single(r => r.Text == "Light").Checked = true;
                Assert.False(ThemeManager.Current.IsDark);
                Controls(form).OfType<ThemedButton>().Single(b => b.Text == "Cancel").PerformClick();
                form.Close();
                Ui.Pump();
            }

            Assert.True(ThemeManager.Current.IsDark);
            ThemeManager.SetPreference(ThemePreference.Light);
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
