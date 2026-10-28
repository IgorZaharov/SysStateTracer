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
/// W1 Main window (plan §10.1, Steps 43–45): sidebar, snapshot list read from file headers, sort order,
/// folder watcher, delete / rename / properties, keyboard shortcuts, and drag-and-drop compare.
/// </summary>
internal sealed class MainForm : Form
{
    private const string DragFormat = "SysStateTracer.SnapshotPath";

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly ThemedListView _list = new() { Dock = DockStyle.Fill, MultiSelect = true, AllowDrop = true };
    private readonly ComboBox _sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label _empty = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly SideBarButton _new = new(Glyphs.Add, "New Snapshot (Ctrl+N)");
    private readonly SideBarButton _delete = new(Glyphs.Delete, "Delete Snapshot (Del)");
    private readonly SideBarButton _preferences = new(Glyphs.Settings, "Preferences");
    private readonly SideBarButton _help = new(Glyphs.Help, "Help / About (F1)");
    private readonly SideBarButton _exit = new(Glyphs.Exit, "Exit");
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

    public MainForm(AppPaths paths, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        ClientSize = new Size(980, 600);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Text = AppInfo.Name;
        Icon = AppIcon.Value;
        KeyPreview = true;

        BuildLayout();
        BuildMenu();
        ResumeLayout(performLayout: true);

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
        if (_settings.SortOrder == SnapshotSortOrder.Name)
        {
            _snapshots.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }

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

        _list.EndUpdate();
        if (_list.SelectedItems.Count > 0)
        {
            _list.SelectedItems[0].EnsureVisible();
            _list.FocusedItem = _list.SelectedItems[0];
        }

        _empty.Visible = _snapshots.Count == 0;
        _empty.Text = $"No snapshots yet in{Environment.NewLine}{SnapshotFolder}{Environment.NewLine}{Environment.NewLine}Click + to take the first one.";
        UpdateStatus();
        UpdateCommands();
    }

    /// <summary>Opens W4 for two snapshots; the older one goes on the left (plan §10.1).</summary>
    internal void Compare(SnapshotInfo a, SnapshotInfo b)
    {
        var (older, newer) = a.Meta.StartedAt <= b.Meta.StartedAt ? (a, b) : (b, a);
        var window = new ComparisonForm(_paths, _settings, older.Path, newer.Path);
        window.Show(this);
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
        // Sidebar: actions top to bottom, Exit pinned at the bottom (plan §10.1).
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 48, Padding = new Padding(0, 4, 0, 4) };
        ThemeManager.SetRole(sidebar, ThemeRole.Surface);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        ThemeManager.SetRole(top, ThemeRole.Surface);
        foreach (var button in new[] { _new, _delete, _preferences, _help })
        {
            top.Controls.Add(button);
            _toolTip.SetToolTip(button, button.Text);
        }

        _exit.Dock = DockStyle.Bottom;
        _toolTip.SetToolTip(_exit, _exit.Text);
        var exitHolder = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(4, 0, 4, 4) };
        ThemeManager.SetRole(exitHolder, ThemeRole.Surface);
        exitHolder.Controls.Add(_exit);
        sidebar.Controls.Add(top);
        sidebar.Controls.Add(exitHolder);

        _new.Click += (_, _) => NewSnapshot();
        _delete.Click += (_, _) => DeleteSelected();
        _preferences.Click += (_, _) => ShowPreferences();
        _help.Click += (_, _) => ShowAbout();
        _exit.Click += (_, _) => Close();

        // Header: title and sort order.
        var header = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 12, 16, 8) };
        var title = new Label { Text = "Snapshots", AutoSize = true, Location = new Point(16, 14) };
        ThemeManager.SetRole(title, ThemeRole.Heading);
        var sortLabel = new Label { Text = "Sort by", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        ThemeManager.SetRole(sortLabel, ThemeRole.Secondary);
        _sort.Items.AddRange(["Date (newest first)", "Name (A–Z)"]);
        _sort.SelectedIndex = _settings.SortOrder == SnapshotSortOrder.Name ? 1 : 0;
        _sort.SelectedIndexChanged += (_, _) =>
        {
            _settings.SortOrder = _sort.SelectedIndex == 1 ? SnapshotSortOrder.Name : SnapshotSortOrder.Date;
            SaveSettings();
            Reload();
        };
        header.Controls.Add(title);
        header.Controls.Add(_sort);
        header.Controls.Add(sortLabel);
        header.Resize += (_, _) =>
        {
            _sort.Location = new Point(header.Width - header.Padding.Right - _sort.Width, 14);
            sortLabel.Location = new Point(_sort.Left - sortLabel.Width - 8, 17);
        };

        // List.
        _list.Columns.Add("Name", 300);
        _list.Columns.Add("Created", 150);
        _list.Columns.Add("Computer", 140);
        _list.Columns.Add("Duration", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
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

        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0) };
        listHolder.Controls.Add(_list);
        listHolder.Controls.Add(_empty);
        _empty.Dock = DockStyle.Fill;
        _empty.BringToFront();
        ThemeManager.SetRole(_empty, ThemeRole.Secondary);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(16, 0, 16, 4) };
        ThemeManager.SetRole(_status, ThemeRole.Secondary);
        footer.Controls.Add(_status);
        var line = new Separator { Dock = DockStyle.Top };
        footer.Controls.Add(line);

        Controls.Add(listHolder);
        Controls.Add(footer);
        Controls.Add(header);
        Controls.Add(sidebar);
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
        [.. _list.SelectedItems.Cast<ListViewItem>().Select(i => (SnapshotInfo)i.Tag!)];

    private void UpdateCommands()
    {
        _delete.Enabled = _list.SelectedItems.Count > 0;
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
        var selected = SelectedSnapshots();
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
                failed.Add($"{snapshot.Name}: {ex.Message}");
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
        var selected = SelectedSnapshots();
        if (selected.Count != 1)
        {
            return;
        }

        using var window = new PropertiesForm(selected[0].Path);
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

        _dragSource = item.Index;
        var data = new DataObject();
        data.SetData(DragFormat, ((SnapshotInfo)item.Tag!).Path);
        _list.DoDragDrop(data, DragDropEffects.Link);
        _dragSource = -1;
        _list.DropTargetIndex = -1;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = TargetIndex(e);
        var valid = e.Data?.GetDataPresent(DragFormat) == true && target >= 0 && target != _dragSource;
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
        var other = (SnapshotInfo)_list.Items[target].Tag!;
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
