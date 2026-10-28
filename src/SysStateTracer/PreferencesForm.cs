using SysStateTracer.Controls;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Scanning.Svc;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// W5 Preferences (plan §10.5, Step 47, decision F1): snapshot folder and theme; which Global Ignore list is
/// used (Remote with fallback, or Local) and an editor for the local list. The theme applies live; Cancel undoes it.
/// </summary>
internal sealed class PreferencesForm : ThemedDialog
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly ThemePreference _originalTheme;
    private readonly LocalIgnoreFile _localFile;
    private readonly List<string> _localLines;

    private readonly TextBox _folder = new() { BorderStyle = BorderStyle.FixedSingle, Width = 420 };
    private readonly RadioButton _themeSystem = new() { Text = "Follow Windows", AutoSize = true };
    private readonly RadioButton _themeLight = new() { Text = "Light", AutoSize = true };
    private readonly RadioButton _themeDark = new() { Text = "Dark", AutoSize = true };
    private readonly CheckBox _remote = new() { Text = "Also use the shared remote list (a URL or network file, shared by several PCs)", AutoSize = true };
    private readonly TextBox _remoteLocation = new() { BorderStyle = BorderStyle.FixedSingle, Width = 470 };
    private readonly Label _remoteResult = new() { AutoSize = true, MaximumSize = new Size(600, 0), Text = " " };
    private readonly ThemedListView _rules = new() { Dock = DockStyle.Fill, MultiSelect = true };
    private readonly Label _rulesCaption = new() { AutoSize = true };
    private readonly Panel _generalPage = new() { Dock = DockStyle.Fill };
    private readonly Panel _ignorePage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ThemedButton _generalTab = new() { Text = "General", Width = 110 };
    private readonly ThemedButton _ignoreTab = new() { Text = "Global Ignore list", Width = 150 };

    public PreferencesForm(AppPaths paths, AppSettings settings)
        : base("Preferences")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        _originalTheme = ThemeManager.Preference;
        _localFile = new LocalIgnoreFile(paths.LocalIgnoreFile);
        _localLines = ReadLocalLines();

        FormBorderStyle = FormBorderStyle.Sizable;
        // Window size, not client size: no window is taller than 580 px at 100 % scaling (F14).
        Size = new Size(720, 580);
        MinimumSize = new Size(640, 460);
        WindowLayout.Remember(this, "Preferences", paths, settings, keepPosition: false);

        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 12), WrapContents = false };
        _generalTab.Margin = new Padding(0, 0, 8, 0);
        _ignoreTab.Margin = new Padding(0);
        tabs.Controls.Add(_generalTab);
        tabs.Controls.Add(_ignoreTab);
        _generalTab.Click += (_, _) => ShowPage(general: true);
        _ignoreTab.Click += (_, _) => ShowPage(general: false);

        var pages = new Panel { Dock = DockStyle.Fill };
        pages.Controls.Add(_generalPage);
        pages.Controls.Add(_ignorePage);
        Controls.Add(pages);
        Controls.Add(tabs);
        pages.BringToFront();

        BuildGeneralPage();
        BuildIgnorePage();

        AddButton("Cancel", DialogResult.Cancel);
        var ok = AddButton("OK", DialogResult.OK, primary: true);
        ok.Click += (_, _) => Save();
        ShowPage(general: true);
    }

    /// <summary>Opens on the Global Ignore list page (tests, and "Add to Global Ignore list" follow-ups).</summary>
    internal void ShowIgnorePage() => ShowPage(general: false);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult != DialogResult.OK && ThemeManager.Preference != _originalTheme)
        {
            ThemeManager.SetPreference(_originalTheme);
        }

        base.OnFormClosed(e);
    }

    private static string RuleType(IgnoreTarget target) => target switch
    {
        IgnoreTarget.FileSystem => "Folder or file",
        IgnoreTarget.Registry => "Registry",
        _ => "Service",
    };

    private void ShowPage(bool general)
    {
        _generalPage.Visible = general;
        _ignorePage.Visible = !general;
        _generalTab.Primary = general;
        _ignoreTab.Primary = !general;
        _generalTab.Invalidate();
        _ignoreTab.Invalidate();
    }

    private void BuildGeneralPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        var folderTitle = new Label { Text = "Snapshot folder", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        ThemeManager.SetRole(folderTitle, ThemeRole.Heading);
        layout.Controls.Add(folderTitle);

        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _folder.Text = _paths.SnapshotDirectory(_settings);
        var browse = new ThemedButton { Text = "Browse…", Margin = new Padding(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Folder for snapshots" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _folder.Text = dialog.SelectedPath;
            }
        };
        var reset = new ThemedButton { Text = "Program folder", Margin = new Padding(8, 0, 0, 0), Width = 120 };
        reset.Click += (_, _) => _folder.Text = _paths.DataDirectory;
        _folder.Margin = new Padding(0, 4, 0, 0);
        folderRow.Controls.AddRange([_folder, browse, reset]);
        layout.Controls.Add(folderRow);
        var folderHint = new Label { Text = "New snapshots are saved here, and the main window lists the snapshots in it.", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        ThemeManager.SetRole(folderHint, ThemeRole.Secondary);
        layout.Controls.Add(folderHint);

        var themeTitle = new Label { Text = "Theme", AutoSize = true, Margin = new Padding(0, 24, 0, 6) };
        ThemeManager.SetRole(themeTitle, ThemeRole.Heading);
        layout.Controls.Add(themeTitle);
        // A picture above each choice (F5); clicking the picture chooses it too.
        var themes = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
        var previews = new List<ThemePreview>();
        foreach (var (radio, theme) in new[] { (_themeSystem, ThemePreference.System), (_themeLight, ThemePreference.Light), (_themeDark, ThemePreference.Dark) })
        {
            var preview = new ThemePreview(theme) { Margin = new Padding(0, 0, 0, 6) };
            var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 20, 0) };
            column.Controls.Add(preview);
            column.Controls.Add(radio);
            radio.Margin = new Padding(2, 0, 0, 0);
            preview.Click += (_, _) => radio.Checked = true;
            previews.Add(preview);
            themes.Controls.Add(column);
        }

        layout.Controls.Add(themes);
        (ThemeManager.Preference switch
        {
            ThemePreference.Light => _themeLight,
            ThemePreference.Dark => _themeDark,
            _ => _themeSystem,
        }).Checked = true;
        void MarkSelected()
        {
            var selected = SelectedTheme();
            foreach (var preview in previews)
            {
                preview.Selected = preview.Theme == selected;
            }
        }

        MarkSelected();
        RadioButton[] themeRadios = [_themeSystem, _themeLight, _themeDark];
        foreach (var radio in themeRadios)
        {
            radio.CheckedChanged += (_, _) =>
            {
                if (radio.Checked)
                {
                    // Each choice sits in its own column under its picture, so Windows does not group the
                    // three: clear the other two here (F13).
                    foreach (var other in themeRadios.Where(r => r != radio))
                    {
                        other.Checked = false;
                    }

                    MarkSelected();
                    ThemeManager.SetPreference(SelectedTheme());
                }
            };
        }

        _generalPage.Controls.Add(layout);
    }

    private ThemePreference SelectedTheme() =>
        _themeLight.Checked ? ThemePreference.Light : _themeDark.Checked ? ThemePreference.Dark : ThemePreference.System;

    private void BuildIgnorePage()
    {
        // The local list always applies; the remote one too unless unticked (F22).
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        var title = new Label { Text = "Lists in use", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        ThemeManager.SetRole(title, ThemeRole.Heading);
        top.Controls.Add(title);
        var always = new Label { Text = "The local list below (SysStateTracer.ignore.txt beside the program) is always used.", AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 0, 0, 6) };
        top.Controls.Add(always);
        top.Controls.Add(_remote);
        var remoteRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(20, 4, 0, 0) };
        _remoteLocation.Text = _settings.IgnoreListRemoteLocation;
        _remoteLocation.Margin = new Padding(0, 4, 0, 0);
        var check = new ThemedButton { Text = "Check now", Margin = new Padding(8, 0, 0, 0) };
        check.Click += async (_, _) => await CheckRemoteAsync(check);
        var defaultButton = new ThemedButton { Text = "Default", Margin = new Padding(8, 0, 0, 0) };
        defaultButton.Click += (_, _) => _remoteLocation.Text = RemoteIgnoreSource.DefaultLocation;
        remoteRow.Controls.AddRange([_remoteLocation, check, defaultButton]);
        top.Controls.Add(remoteRow);
        _remoteResult.Margin = new Padding(20, 4, 0, 8);
        ThemeManager.SetRole(_remoteResult, ThemeRole.Secondary);
        top.Controls.Add(_remoteResult);
        var fallback = new Label
        {
            Text = "Rules from both lists apply. When the remote list cannot be read, the local list is used alone. \"Add to Global Ignore list\" in the comparison window adds to the local list.",
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Margin = new Padding(0, 6, 0, 16),
        };
        ThemeManager.SetRole(fallback, ThemeRole.Secondary);
        top.Controls.Add(fallback);
        _remote.Checked = _settings.IgnoreListSource == IgnoreListSource.Remote;
        _remote.CheckedChanged += (_, _) => _remoteLocation.Enabled = _remote.Checked;
        _remoteLocation.Enabled = _remote.Checked;

        _rulesCaption.Margin = new Padding(0, 0, 0, 6);
        ThemeManager.SetRole(_rulesCaption, ThemeRole.Heading);
        top.Controls.Add(_rulesCaption);

        // The local list's rules.
        _rules.Columns.Add("Type", 120);
        _rules.Columns.Add("Rule", 400);
        _rules.ItemColor = (item, _) => item.Tag is string ? ThemeManager.Current.Deleted : null;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0), WrapContents = true };
        var addRule = new ThemedButton { Text = "Add rule…", Width = 100 };
        var editRule = new ThemedButton { Text = "Edit…", Width = 80 };
        var addFolder = new ThemedButton { Text = "Add folder…", Width = 110 };
        var addKey = new ThemedButton { Text = "Add registry key…", Width = 140 };
        var addService = new ThemedButton { Text = "Add service…", Width = 110 };
        var remove = new ThemedButton { Text = "Remove", Width = 90 };
        var import = new ThemedButton { Text = "Import…", Width = 90 };
        var export = new ThemedButton { Text = "Export…", Width = 90 };
        foreach (var b in new[] { addRule, editRule, addFolder, addKey, addService, remove, import, export })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(b);
        }

        addRule.Click += (_, _) => AddTypedRules();
        editRule.Click += (_, _) => EditSelected();
        _rules.DoubleClick += (_, _) => EditSelected();
        addFolder.Click += (_, _) => AddFolder();
        addKey.Click += (_, _) => AddRegistryKey();
        addService.Click += (_, _) => AddService();
        remove.Click += (_, _) => RemoveSelected();
        import.Click += (_, _) => Import();
        export.Click += (_, _) => Export();
        _rules.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
            }
            else if (e.KeyCode is Keys.F2 or Keys.Enter)
            {
                EditSelected();
                e.Handled = true;
            }
        };

        var rulesHolder = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        rulesHolder.Controls.Add(_rules);
        _ignorePage.Controls.Add(rulesHolder);
        _ignorePage.Controls.Add(buttons);
        _ignorePage.Controls.Add(top);
        rulesHolder.BringToFront();
        FillRules();
    }

    private List<string> ReadLocalLines()
    {
        try
        {
            return [.. _localFile.ReadText().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not read {_localFile.Path}", ex);
            return [.. DefaultIgnoreRules.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        }
    }

    /// <summary>Shows the rule lines of the local file (comments and blank lines are kept in the file but not listed).</summary>
    private void FillRules()
    {
        _rules.BeginUpdate();
        _rules.Items.Clear();
        var count = 0;
        for (var i = 0; i < _localLines.Count; i++)
        {
            var line = _localLines[i];
            if (IgnoreRuleParser.IsBlankOrComment(line))
            {
                continue;
            }

            var item = IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out var rule, out var error)
                ? new ListViewItem(RuleType(rule.Target))
                : new ListViewItem("Not valid") { Tag = error, ToolTipText = error };
            item.SubItems.Add(line.Trim());
            item.Name = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _rules.Items.Add(item);
            count++;
        }

        _rules.EndUpdate();
        _rulesCaption.Text = $"Local list ({count} rules)";
    }

    private void AddRule(string line)
    {
        var trimmed = line.Trim();
        if (!IgnoreRuleParser.TryParse(trimmed, IgnoreRuleSource.Local, out _, out var error))
        {
            MessageDialog.Inform(this, "Global Ignore list", $"\"{trimmed}\" is not a valid rule: {error}");
            return;
        }

        if (_localLines.Any(l => string.Equals(l.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Keep a trailing blank line at the end of the file.
        var insertAt = _localLines.Count;
        while (insertAt > 0 && string.IsNullOrWhiteSpace(_localLines[insertAt - 1]))
        {
            insertAt--;
        }

        _localLines.Insert(insertAt, trimmed);
        FillRules();
        var added = _rules.Items.Cast<ListViewItem>().LastOrDefault(i => i.SubItems[1].Text == trimmed);
        if (added is not null)
        {
            added.Selected = true;
            added.EnsureVisible();
        }
    }

    /// <summary>Rules typed in, e.g. with wildcards (F25).</summary>
    private void AddTypedRules()
    {
        var rules = IgnoreRulesDialog.Edit(this, "Add rules", "Rules to add to the local list:", ["FS:"], "Add");
        foreach (var rule in rules ?? [])
        {
            AddRule(rule);
        }
    }

    /// <summary>Changes the selected rule in place (F25); a rule can become several lines.</summary>
    internal void EditSelected()
    {
        if (_rules.SelectedItems.Count != 1)
        {
            return;
        }

        var index = int.Parse(_rules.SelectedItems[0].Name, System.Globalization.CultureInfo.InvariantCulture);
        var edited = IgnoreRulesDialog.Edit(this, "Edit rule", "Change the rule:", [_localLines[index].Trim()], "OK");
        if (edited is not { Count: > 0 })
        {
            return;
        }

        ReplaceRule(index, edited);
    }

    /// <summary>Puts <paramref name="rules"/> in place of line <paramref name="index"/> of the local list.</summary>
    internal void ReplaceRule(int index, IReadOnlyList<string> rules)
    {
        _localLines.RemoveAt(index);
        var fresh = rules
            .Where(r => !_localLines.Any(l => string.Equals(l.Trim(), r, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _localLines.InsertRange(index, fresh);
        FillRules();
        var first = _rules.Items.Cast<ListViewItem>().FirstOrDefault(i => i.SubItems[1].Text == rules[0]);
        if (first is not null)
        {
            first.Selected = true;
            first.EnsureVisible();
        }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Folder to leave out of snapshots and comparisons" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddRule(IgnoreRuleParser.Format(IgnoreTarget.FileSystem, dialog.SelectedPath));
        }
    }

    private void AddRegistryKey()
    {
        var key = InputDialog.Ask(this, "Add registry key", "Key path, e.g. HKLM\\SOFTWARE\\Vendor\\App\\Cache or HKCU\\Software\\App\\MRU:", "HKLM\\SOFTWARE\\", "Add", text =>
            IgnoreRuleParser.TryParse(IgnoreRuleParser.Format(IgnoreTarget.Registry, text), IgnoreRuleSource.Local, out _, out var error) ? null : error);
        if (key is not null)
        {
            AddRule(IgnoreRuleParser.Format(IgnoreTarget.Registry, key));
        }
    }

    private void AddService()
    {
        List<(string Name, string DisplayName)> services;
        try
        {
            services = ServicesScanner.ListInstalled();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageDialog.Error(this, "The list of services could not be read.", ex);
            return;
        }

        using var picker = new ServicePickerDialog(services);
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var name in picker.SelectedNames)
            {
                AddRule(IgnoreRuleParser.Format(IgnoreTarget.Service, name));
            }
        }
    }

    private void RemoveSelected()
    {
        var indexes = _rules.SelectedItems.Cast<ListViewItem>()
            .Select(i => int.Parse(i.Name, System.Globalization.CultureInfo.InvariantCulture))
            .OrderDescending()
            .ToList();
        foreach (var index in indexes)
        {
            _localLines.RemoveAt(index);
        }

        if (indexes.Count > 0)
        {
            FillRules();
        }
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Import rules" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var (rules, errors) = IgnoreRuleParser.ParseText(File.ReadAllText(dialog.FileName), IgnoreRuleSource.Local);
            foreach (var rule in rules)
            {
                AddRule(rule.Text);
            }

            MessageDialog.Inform(
                this,
                "Import rules",
                $"{rules.Count} rules read from {Path.GetFileName(dialog.FileName)}" + (errors.Count > 0 ? $"; {errors.Count} lines were not valid rules and were left out." : "."),
                errors.Count > 0 ? [.. errors.Select(e => $"Line {e.LineNumber}: {e.Line}  ({e.Message})")] : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "The file could not be read.", ex);
        }
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", FileName = "SysStateTracer.ignore.txt", Title = "Export rules" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _localLines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "The file could not be written.", ex);
        }
    }

    private async Task CheckRemoteAsync(ThemedButton button)
    {
        button.Enabled = false;
        _remoteResult.Text = "Reading…";
        try
        {
            using var http = new HttpClient();
            var fetch = await RemoteIgnoreSource.FetchAsync(_remoteLocation.Text.Trim(), http);
            if (fetch.Succeeded)
            {
                var (rules, errors) = IgnoreRuleParser.ParseText(fetch.Text!, IgnoreRuleSource.Remote);
                _remoteResult.Text = $"OK: {rules.Count} rules" + (errors.Count > 0 ? $", {errors.Count} lines not valid" : string.Empty) + $". {fetch.Message}";
                _remoteResult.ForeColor = ThemeManager.Current.New;
            }
            else
            {
                _remoteResult.Text = fetch.Message + " Only the local list would be used.";
                _remoteResult.ForeColor = ThemeManager.Current.Deleted;
            }
        }
        finally
        {
            if (!button.IsDisposed)
            {
                button.Enabled = true;
            }
        }
    }

    private void Save()
    {
        // Check the folder first, so nothing changes when it cannot be used.
        string? folder = _folder.Text.Trim();
        try
        {
            folder = folder.Length == 0 || string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), _paths.DataDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.GetFullPath(folder);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
                if (!AppPaths.IsDirectoryWritable(folder))
                {
                    MessageDialog.Inform(this, "Preferences", $"SysStateTracer cannot write to {folder}. Choose another snapshot folder.");
                    DialogResult = DialogResult.None;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageDialog.Error(this, "The snapshot folder cannot be used.", ex);
            DialogResult = DialogResult.None;
            return;
        }

        _settings.SnapshotFolder = folder;
        _settings.Theme = SelectedTheme();
        _settings.IgnoreListSource = _remote.Checked ? IgnoreListSource.Remote : IgnoreListSource.Local;
        var location = _remoteLocation.Text.Trim();
        _settings.IgnoreListRemoteLocation = location.Length == 0 ? RemoteIgnoreSource.DefaultLocation : location;

        try
        {
            SettingsStore.Save(_paths.ConfigFile, _settings);
            _localFile.Save(string.Join(Environment.NewLine, _localLines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "Preferences could not be saved.", ex);
            DialogResult = DialogResult.None;
        }
    }
}

/// <summary>Pick installed services for the Global Ignore list.</summary>
internal sealed class ServicePickerDialog : ThemedDialog
{
    private readonly ThemedListView _list = new() { Dock = DockStyle.Fill, MultiSelect = true };

    public ServicePickerDialog(IReadOnlyList<(string Name, string DisplayName)> services)
        : base("Add service")
    {
        ClientSize = new Size(560, 480);
        _list.Columns.Add("Display name", 320);
        _list.Columns.Add("Service name", 200);
        foreach (var (name, display) in services)
        {
            var item = new ListViewItem(display) { Tag = name };
            item.SubItems.Add(name);
            _list.Items.Add(item);
        }

        Controls.Add(_list);
        _list.BringToFront();
        AddButton("Cancel", DialogResult.Cancel);
        AddButton("Add", DialogResult.OK, primary: true);
        _list.DoubleClick += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    public IReadOnlyList<string> SelectedNames => [.. _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!)];
}
