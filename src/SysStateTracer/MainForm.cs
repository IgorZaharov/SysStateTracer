using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Core.Storage;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// W1 Main window (plan §10.1, Steps 43–45): command bar, snapshot list read from file headers, sort order,
/// folder watcher, compare / delete / rename / properties, keyboard shortcuts, and drag-and-drop compare.
/// Windows 11 layout: the commands on the window's Mica tint, the list on a layer card.
/// </summary>
internal sealed class MainForm : Form
{
    private const string DragFormat = "SysStateTracer.SnapshotPath";

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly ThemedListView _list = new() { Dock = DockStyle.Fill, MultiSelect = true, AllowDrop = true, ShowItemToolTips = true };
    private readonly Panel _warning = new() { Dock = DockStyle.Top, Height = 60, Padding = new Padding(20, 0, 20, 12), Visible = false };
    private readonly InfoBar _warningBar = new()
    {
        Dock = DockStyle.Fill,
        Title = "Not running as administrator",
        Message = "Snapshots miss the folders and registry keys that need administrator rights, and whole drives are read more slowly.",
    };
    private readonly ThemedButton _elevate = new() { Text = "Restart as administrator", Glyph = Glyphs.Shield };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = UiFonts.Caption };
    private readonly Label _hint = new() { AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Font = UiFonts.Caption, Text = "Drag one snapshot onto another to compare them" };
    private readonly Label _empty = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private Control? _commands;
    private readonly ThemedButton _new =new() { Text = "New snapshot", Glyph = Glyphs.Add, Primary = true };
    private readonly ThemedButton _compare = new() { Text = "Compare", Glyph = Glyphs.Compare };
    private readonly ThemedButton _renameButton = new() { Text = "Rename", Glyph = Glyphs.Rename, Subtle = true };
    private readonly ThemedButton _delete = new() { Text = "Delete", Glyph = Glyphs.Delete, Subtle = true };
    private readonly ThemedButton _propertiesButton = new() { Text = "Properties", Glyph = Glyphs.Info, Subtle = true };
    private readonly ThemedButton _preferences = new() { Text = string.Empty, Glyph = Glyphs.Settings, Subtle = true, AccessibleName = "Preferences" };
    private readonly ThemedButton _help = new() { Text = string.Empty, Glyph = Glyphs.Help, Subtle = true, AccessibleName = "Help / About" };
    private readonly ThemedToolTip _toolTip = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _compareWith = new("Compare with");
    private readonly ToolStripMenuItem _rename = new("Rename Snapshot") { ShortcutKeyDisplayString = "F2" };
    private readonly ToolStripMenuItem _deleteItem = new("Delete Snapshot") { ShortcutKeyDisplayString = "Del" };
    private readonly ToolStripMenuItem _properties = new("Snapshot Properties") { ShortcutKeyDisplayString = "Enter" };
    private readonly System.Windows.Forms.Timer _reloadTimer = new() { Interval = 400 };
    private FileSystemWatcher? _watcher;
    private List<SnapshotInfo> _snapshots = [];
    private List<UnreadableSnapshot> _unreadable = [];
    private int _dragSource = -1;
    private string? _selectAfterReload;

    public MainForm(AppPaths paths, AppSettings settings, bool? elevated = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        Size = new Size(850, 450); // F14
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        WindowLayout.Remember(this, "Main", paths, settings);
        Text = AppInfo.Name;
        Icon = AppIcon.Value;
        KeyPreview = true;

        BuildLayout();
        BuildMenu();
        _warning.Controls.Add(_warningBar);
        ThemeManager.SetRole(_warning, ThemeRole.Surface);
        _warningBar.AddAction(_elevate);
        _warningBar.Closed += (_, _) => _warning.Visible = false;
        _elevate.Click += (_, _) =>
        {
            if (Elevation.RestartAsAdministrator(this))
            {
                Close();
            }
        };
        _warning.Visible = !(elevated ?? Core.Interop.ProcessPrivileges.IsElevated);
        ResumeLayout(performLayout: true);

        ThemeManager.SetRole(this, ThemeRole.Surface);
        ThemeManager.Attach(this);
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
    }

    /// <summary>Folder the list shows (Preferences, or the program folder).</summary>
    public string SnapshotFolder => _paths.SnapshotDirectory(_settings);

    /// <summary>Snapshots in list order (tests).</summary>
    internal IReadOnlyList<SnapshotInfo> Snapshots => _snapshots;

    internal ThemedListView List => _list;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Reload();
        StartWatcher();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watcher?.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_list.Focused || ActiveControl == _list || keyData is (Keys.Control | Keys.N) or Keys.F1 or Keys.F5)
        {
            switch (keyData)
            {
                case Keys.Delete:
                    DeleteSelected();
                    return true;
                case Keys.F2:
                    RenameSelected();
                    return true;
                case Keys.Enter:
                    ShowProperties();
                    return true;
                case Keys.F5:
                    Reload();
                    return true;
                case Keys.F1:
                    ShowAbout();
                    return true;
                case Keys.Control | Keys.N:
                    NewSnapshot();
                    return true;
                case Keys.Control | Keys.A:
                    _list.BeginUpdate();
                    foreach (ListViewItem item in _list.Items)
                    {
                        item.Selected = true;
                    }

                    _list.EndUpdate();
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Re-reads the snapshot headers; keeps the selection (or selects <see cref="_selectAfterReload"/>).</summary>
    internal void Reload()
    {
        var selected = SelectedSnapshots().Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_selectAfterReload is not null)
        {
            selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _selectAfterReload };
            _selectAfterReload = null;
        }

        var unreadable = new List<UnreadableSnapshot>();
        try
        {
            _snapshots = new SnapshotCatalog(SnapshotFolder).List(unreadable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _snapshots = [];
            Log.Warning($"Snapshot folder cannot be read: {SnapshotFolder}", ex);
        }

        _unreadable = unreadable;
        SortSnapshots();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var info in _snapshots)
        {
            var meta = info.Meta;
            var item = new ListViewItem(info.Name) { Tag = info };
            item.SubItems.Add(meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            item.SubItems.Add(meta.ComputerName);
            item.SubItems.Add(Duration(meta.Timings.TotalMs));
            item.SubItems.Add(ItemFormat.Size(info.FileLength));
            item.Selected = selected.Contains(info.Path);
            _list.Items.Add(item);
        }

        // Damaged or newer-format files stay visible, so they can be deleted (plan Step 53).
        foreach (var bad in _unreadable)
        {
            var item = new ListViewItem(Path.GetFileNameWithoutExtension(bad.Path)) { Tag = bad, ToolTipText = bad.Reason };
            item.SubItems.Add("Cannot be read");
            item.SubItems.Add(string.Empty);
            item.SubItems.Add(string.Empty);
            item.SubItems.Add(FileSize(bad.Path));
            item.Selected = selected.Contains(bad.Path);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        if (_list.SelectedItems.Count > 0)
        {
            _list.SelectedItems[0].EnsureVisible();
            _list.FocusedItem = _list.SelectedItems[0];
        }

        _empty.Visible = _snapshots.Count == 0 && _unreadable.Count == 0;
        _empty.Text = $"No snapshots yet in{Environment.NewLine}{SnapshotFolder}{Environment.NewLine}{Environment.NewLine}Click New snapshot to take the first one.";
        UpdateStatus();
        UpdateCommands();
    }

    /// <summary>Column order in the list, and the sort each header click chooses (F16).</summary>
    private static readonly SnapshotSortOrder[] ColumnSort =
        [SnapshotSortOrder.Name, SnapshotSortOrder.Date, SnapshotSortOrder.Computer, SnapshotSortOrder.Duration, SnapshotSortOrder.Size];

    /// <summary>
    /// Clicking a header sorts by that column; clicking it again reverses the order. Dates, durations and sizes
    /// start with the newest, longest or largest; text starts with A.
    /// </summary>
    internal void SortBy(int column)
    {
        var order = ColumnSort[column];
        if (order == _settings.SortOrder)
        {
            _settings.SortDescending = !_settings.SortDescending;
        }
        else
        {
            _settings.SortOrder = order;
            _settings.SortDescending = order is SnapshotSortOrder.Date or SnapshotSortOrder.Duration or SnapshotSortOrder.Size;
        }

        SaveSettings();
        Reload();
    }

    private void SortSnapshots()
    {
        Comparison<SnapshotInfo> compare = _settings.SortOrder switch
        {
            SnapshotSortOrder.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            SnapshotSortOrder.Computer => (a, b) => string.Compare(a.Meta.ComputerName, b.Meta.ComputerName, StringComparison.CurrentCultureIgnoreCase),
            SnapshotSortOrder.Duration => (a, b) => a.Meta.Timings.TotalMs.CompareTo(b.Meta.Timings.TotalMs),
            SnapshotSortOrder.Size => (a, b) => a.FileLength.CompareTo(b.FileLength),
            _ => (a, b) => a.Meta.StartedAt.CompareTo(b.Meta.StartedAt),
        };
        var sign = _settings.SortDescending ? -1 : 1;

        // Equal values keep the newest first.
        _snapshots.Sort((a, b) =>
        {
            var c = sign * compare(a, b);
            return c != 0 ? c : b.Meta.StartedAt.CompareTo(a.Meta.StartedAt);
        });
        _list.SortColumn = Array.IndexOf(ColumnSort, _settings.SortOrder);
        _list.SortDescending = _settings.SortDescending;
    }

    /// <summary>Opens W4 for two snapshots; the older one goes on the left (plan §10.1).</summary>
    internal void Compare(SnapshotInfo a, SnapshotInfo b)
    {
        var (older, newer) = a.Meta.StartedAt <= b.Meta.StartedAt ? (a, b) : (b, a);
        var window = new ComparisonForm(_paths, _settings, older.Path, newer.Path);
        window.Show(this);
    }

    /// <summary>The command buttons, left to right.</summary>
    internal ThemedButton[] CommandButtons => [_new, _compare, _renameButton, _delete, _propertiesButton];

    internal Control? CommandBar => _commands;

    /// <summary>
    /// When the window is too narrow for every label, the buttons from the right end show only their icon (the tooltip
    /// still names them), as in the Windows 11 command bar; New snapshot keeps its label.
    /// </summary>
    internal void FitCommands()
    {
        if (_commands is null)
        {
            return;
        }

        var buttons = CommandButtons;
        var full = buttons.Select(b => b.PreferredButtonWidth()).ToArray();
        var need = buttons.Select((b, i) => full[i] + b.Margin.Horizontal).Sum();
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var button = buttons[i];
            var compact = i > 0 && need > _commands.ClientSize.Width;
            if (compact)
            {
                need -= full[i] - button.Height;
            }

            button.IconOnly = compact;
            button.Width = compact ? button.Height : full[i];
        }
    }

    private static string Duration(long ms) => ms switch
    {
        <= 0 => string.Empty,
        < 60_000 => string.Create(CultureInfo.CurrentCulture, $"{ms / 1000.0:0.0} s"),
        < 3_600_000 => string.Create(CultureInfo.CurrentCulture, $"{ms / 60_000} min {ms / 1000 % 60} s"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{ms / 3_600_000} h {ms / 60_000 % 60} min"),
    };

    private void BuildLayout()
    {
        // Command bar on the Mica tint: title, the snapshot commands, then Preferences and Help at the right end
        // (Windows 11 keeps a left pane for moving between pages; this window has one page, so its commands go on top).
        var header = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(20, 12, 16, 12) };
        ThemeManager.SetRole(header, ThemeRole.Surface);
        var title = new Label { Text = "Snapshots", AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 0, 16, 0) };
        ThemeManager.SetRole(title, ThemeRole.Heading);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false, Padding = new Padding(0), Margin = new Padding(0) };
        ThemeManager.SetRole(commands, ThemeRole.Surface);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Right, WrapContents = false, AutoSize = true, Padding = new Padding(0), Margin = new Padding(0) };
        ThemeManager.SetRole(tools, ThemeRole.Surface);
        foreach (var button in new[] { _new, _compare, _renameButton, _delete, _propertiesButton })
        {
            button.MinimumSize = new Size(32, 32);
            button.Width = button.PreferredButtonWidth();
            button.Margin = new Padding(0, 0, 8, 0);
            commands.Controls.Add(button);
        }

        foreach (var button in new[] { _preferences, _help })
        {
            button.MinimumSize = new Size(32, 32);
            button.Size = new Size(32, 32);
            button.Margin = new Padding(4, 0, 0, 0);
            tools.Controls.Add(button);
        }

        _toolTip.SetToolTip(_new, "New snapshot (Ctrl+N)");
        _toolTip.SetToolTip(_compare, "Compare the two selected snapshots, or the selected one with another");
        _toolTip.SetToolTip(_renameButton, "Rename (F2)");
        _toolTip.SetToolTip(_delete, "Delete (Del)");
        _toolTip.SetToolTip(_propertiesButton, "Properties (Enter)");
        _toolTip.SetToolTip(_preferences, "Preferences");
        _toolTip.SetToolTip(_help, "Help / About (F1)");
        header.Controls.Add(commands);
        header.Controls.Add(tools);
        header.Controls.Add(title);
        _commands = commands;
        commands.Resize += (_, _) => FitCommands();

        _new.Click += (_, _) => NewSnapshot();
        _compare.Click += (_, _) => CompareSelected();
        _renameButton.Click += (_, _) => RenameSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _propertiesButton.Click += (_, _) => ShowProperties();
        _preferences.Click += (_, _) => ShowPreferences();
        _help.Click += (_, _) => ShowAbout();

        // List.
        _list.Columns.Add("Name", 300);
        _list.Columns.Add("Created", 150);
        _list.Columns.Add("Computer", 140);
        _list.Columns.Add("Duration", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.HeaderStyle = ColumnHeaderStyle.Clickable;
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.ItemColor = (item, column) => item.Tag is UnreadableSnapshot
            ? (column == 0 ? ThemeManager.Current.Deleted : ThemeManager.Current.SecondaryText)
            : null;
        _list.SelectedIndexChanged += (_, _) =>
        {
            UpdateCommands();
            UpdateStatus();
        };
        _list.DoubleClick += (_, _) => ShowProperties();
        _list.ItemDrag += OnItemDrag;
        _list.DragOver += OnDragOver;
        _list.DragDrop += OnDragDrop;
        _list.DragLeave += (_, _) => _list.DropTargetIndex = -1;

        // The list on a layer card, inset from the window edges.
        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        ThemeManager.SetRole(listHolder, ThemeRole.Surface);
        var card = new LayerPanel { Dock = DockStyle.Fill };
        var inside = new LayerFill { Dock = DockStyle.Fill };
        inside.Controls.Add(_list);
        inside.Controls.Add(_empty);
        _empty.Dock = DockStyle.Fill;
        _empty.BringToFront();
        ThemeManager.SetRole(_empty, ThemeRole.Secondary);
        card.Controls.Add(inside);
        listHolder.Controls.Add(card);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(24, 8, 24, 8) };
        ThemeManager.SetRole(footer, ThemeRole.Surface);
        ThemeManager.SetRole(_status, ThemeRole.Secondary);
        ThemeManager.SetRole(_hint, ThemeRole.Secondary);
        footer.Controls.Add(_status);
        footer.Controls.Add(_hint);

        Controls.Add(listHolder);
        Controls.Add(footer);
        Controls.Add(_warning);
        Controls.Add(header);
    }

    /// <summary>Compare: the two selected snapshots, or the selected one with one picked from a menu.</summary>
    private void CompareSelected()
    {
        var selected = SelectedSnapshots();
        if (selected.Count == 2)
        {
            Compare(selected[0], selected[1]);
            return;
        }

        if (selected.Count == 1)
        {
            FillCompareWith();
            var menu = new ContextMenuStrip();
            foreach (var item in _compareWith.DropDownItems.Cast<ToolStripItem>().ToList())
            {
                menu.Items.Add(item);
            }

            ThemeManager.ApplyMenu(menu, ThemeManager.Current);
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(_compare, new Point(0, _compare.Height));
        }
    }

    private void BuildMenu()
    {
        _compareWith.DropDownOpening += (_, _) => FillCompareWith();
        _compareWith.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        _rename.Click += (_, _) => RenameSelected();
        _deleteItem.Click += (_, _) => DeleteSelected();
        _properties.Click += (_, _) => ShowProperties();
        _menu.Items.AddRange([_compareWith, new ToolStripSeparator(), _rename, _deleteItem, new ToolStripSeparator(), _properties]);
        _menu.Opening += (_, e) =>
        {
            var count = _list.SelectedItems.Count;
            e.Cancel = count == 0;
            _compareWith.Enabled = count == 1 && _snapshots.Count > 1;
            _rename.Enabled = count == 1;
            _properties.Enabled = count == 1;
            _deleteItem.Text = count > 1 ? $"Delete {count} Snapshots" : "Delete Snapshot";
            ThemeManager.ApplyMenu(_menu, ThemeManager.Current);
        };
        _list.ContextMenuStrip = _menu;
    }

    private void FillCompareWith()
    {
        _compareWith.DropDownItems.Clear();
        var source = SelectedSnapshots().FirstOrDefault();
        if (source is null)
        {
            return;
        }

        foreach (var other in _snapshots.Where(s => !string.Equals(s.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
        {
            var target = other;
            var label = $"{other.Name}  ({other.Meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})";
            _compareWith.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => Compare(source, target)));
        }

        ThemeManager.ApplyMenu(_compareWith.DropDown, ThemeManager.Current);
    }

    private List<SnapshotInfo> SelectedSnapshots() =>
        [.. _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<SnapshotInfo>()];

    /// <summary>Selected rows, readable or not, as (name, path).</summary>
    private List<(string Name, string Path)> SelectedFiles() =>
        [.. _list.SelectedItems.Cast<ListViewItem>().Select<ListViewItem, (string Name, string Path)>(i => i.Tag switch
        {
            SnapshotInfo info => (info.Name, info.Path),
            UnreadableSnapshot bad => (System.IO.Path.GetFileNameWithoutExtension(bad.Path), bad.Path),
            _ => (string.Empty, string.Empty),
        }).Where(f => f.Path.Length > 0)];

    private static string FileSize(string path)
    {
        try
        {
            return ItemFormat.Size(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void UpdateCommands()
    {
        var count = _list.SelectedItems.Count;
        var snapshots = SelectedSnapshots().Count;
        _delete.Enabled = count > 0;
        _renameButton.Enabled = snapshots == 1 && count == 1;
        _propertiesButton.Enabled = count == 1;
        _compare.Enabled = snapshots == count && (snapshots == 2 || (snapshots == 1 && _snapshots.Count > 1));
    }

    private void UpdateStatus()
    {
        var total = _snapshots.Sum(s => s.FileLength);
        var text = string.Create(CultureInfo.CurrentCulture, $"{_snapshots.Count:N0} snapshots ({ItemFormat.Size(total)}) in {SnapshotFolder}");
        if (_list.SelectedItems.Count > 1)
        {
            text = string.Create(CultureInfo.CurrentCulture, $"{_list.SelectedItems.Count:N0} selected · ") + text;
        }

        if (_unreadable.Count > 0)
        {
            text += string.Create(CultureInfo.CurrentCulture, $" · {_unreadable.Count} cannot be read (see {AppPaths.LogFileName})");
        }

        _status.Text = text;
    }

    private void NewSnapshot()
    {
        using var window = new NewSnapshotForm(_paths, _settings);
        window.ShowDialog(this);
        if (window.CreatedPath is { } created)
        {
            _selectAfterReload = created;
            Reload();
        }
    }

    private void DeleteSelected()
    {
        var selected = SelectedFiles();
        if (selected.Count == 0)
        {
            return;
        }

        var question = selected.Count == 1
            ? $"Delete snapshot \"{selected[0].Name}\"?"
            : $"Delete these {selected.Count} snapshots?";
        if (!MessageDialog.Ask(this, "Delete Snapshot", question + " The files are deleted permanently.", "Delete", "Cancel", selected.Count > 1 ? [.. selected.Select(s => s.Name)] : null))
        {
            return;
        }

        var failed = new List<string>();
        foreach (var snapshot in selected)
        {
            try
            {
                File.Delete(snapshot.Path);
                Log.Info($"Deleted snapshot {snapshot.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Could not delete {snapshot.Path}", ex);
                failed.Add($"{snapshot.Name}: {FriendlyErrors.Describe(ex)}");
            }
        }

        Reload();
        if (failed.Count > 0)
        {
            MessageDialog.Inform(this, "Delete Snapshot", "Some snapshots could not be deleted.", failed);
        }
    }

    private void RenameSelected()
    {
        var selected = SelectedSnapshots();
        if (selected.Count != 1)
        {
            return;
        }

        var snapshot = selected[0];
        var folder = Path.GetDirectoryName(snapshot.Path)!;
        var name = InputDialog.Ask(this, "Rename Snapshot", "New name:", snapshot.Name, "Rename", candidate =>
        {
            if (!SnapshotNaming.IsValidName(candidate))
            {
                return "Not a valid file name: no \\ / : * ? \" < > | and no trailing dot or space.";
            }

            var target = Path.Combine(folder, candidate + SnapshotNaming.FileExtension);
            return !string.Equals(candidate, snapshot.Name, StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                ? "A snapshot with this name already exists."
                : null;
        });
        if (name is null || name == snapshot.Name)
        {
            return;
        }

        try
        {
            _selectAfterReload = SnapshotCatalog.Rename(snapshot.Path, name);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageDialog.Error(this, $"Could not rename \"{snapshot.Name}\".", ex);
        }
    }

    private void ShowProperties()
    {
        if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is UnreadableSnapshot bad)
        {
            MessageDialog.Inform(this, "Snapshot Properties", $"This file cannot be read:{Environment.NewLine}{bad.Path}{Environment.NewLine}{Environment.NewLine}{bad.Reason}{Environment.NewLine}{Environment.NewLine}You can delete it from the list.");
            return;
        }

        var selected = SelectedSnapshots();
        if (selected.Count != 1)
        {
            return;
        }

        using var window = new PropertiesForm(selected[0].Path, _paths, _settings);
        window.ShowDialog(this);
    }

    private void ShowPreferences()
    {
        using var window = new PreferencesForm(_paths, _settings);
        if (window.ShowDialog(this) == DialogResult.OK)
        {
            StartWatcher();
            Reload();
        }
    }

    private void ShowAbout()
    {
        using var window = new AboutForm(_paths, _settings);
        window.ShowDialog(this);
    }

    private void SaveSettings()
    {
        try
        {
            SettingsStore.Save(_paths.ConfigFile, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Settings could not be saved", ex);
        }
    }

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(SnapshotFolder))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(SnapshotFolder, "*" + SnapshotNaming.FileExtension)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                SynchronizingObject = this,
            };
            FileSystemEventHandler changed = (_, _) => ScheduleReload();
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot watch the snapshot folder {SnapshotFolder}", ex);
        }
    }

    private void ScheduleReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---- Drag one snapshot onto another to compare them (plan §10.1, Step 45) ----

    private void OnItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not ListViewItem item)
        {
            return;
        }

        if (item.Tag is not SnapshotInfo dragged)
        {
            return;
        }

        _dragSource = item.Index;
        var data = new DataObject();
        data.SetData(DragFormat, dragged.Path);
        _list.DoDragDrop(data, DragDropEffects.Link);
        _dragSource = -1;
        _list.DropTargetIndex = -1;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = TargetIndex(e);
        var valid = e.Data?.GetDataPresent(DragFormat) == true && target >= 0 && target != _dragSource && _list.Items[target].Tag is SnapshotInfo;
        e.Effect = valid ? DragDropEffects.Link : DragDropEffects.None;
        _list.DropTargetIndex = valid ? target : -1;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        var target = TargetIndex(e);
        _list.DropTargetIndex = -1;
        if (e.Data?.GetData(DragFormat) is not string sourcePath || target < 0)
        {
            return;
        }

        var source = _snapshots.FirstOrDefault(s => string.Equals(s.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (_list.Items[target].Tag is not SnapshotInfo other)
        {
            return;
        }

        if (source is not null && !string.Equals(source.Path, other.Path, StringComparison.OrdinalIgnoreCase))
        {
            Compare(source, other);
        }
    }

    private int TargetIndex(DragEventArgs e)
    {
        var point = _list.PointToClient(new Point(e.X, e.Y));
        return _list.GetItemAt(point.X, point.Y)?.Index ?? -1;
    }
}
