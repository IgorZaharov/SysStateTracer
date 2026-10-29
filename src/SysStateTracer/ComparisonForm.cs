using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using SysStateTracer.Controls;
using SysStateTracer.Core;
using SysStateTracer.Core.Actions;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Export;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// W4 Snapshot Comparison, two panes (1.1.0): the snapshots and ⇄ / Refresh on top; on the left Show all /
/// Show only changes and the tree of drives, folders, hives and keys; on the right the files, values or services
/// of the selected one (sortable, with Include subfolders), and below them the selected item in both snapshots
/// side by side; the colour legend in the status bar. The context menu of §10.6 works in both panes.
/// </summary>
internal sealed class ComparisonForm : Form
{
    private const int DetailsHeight = 140;

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly string _leftPath;
    private readonly string _rightPath;
    private readonly VirtualTreeGrid _tree = new() { Dock = DockStyle.Fill, ShowHeader = false, TreeLines = true, UseListStyle = false, StretchLastColumn = true };
    private readonly VirtualTreeGrid _list = new() { Dock = DockStyle.Fill, Flat = true, StretchLastColumn = true };
    private readonly DetailsPanel _details = new() { Dock = DockStyle.Fill };
    private readonly ColorLegend _legend = new() { Dock = DockStyle.Right };
    private readonly ThemedSplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1 };
    private readonly ThemedSplitContainer _rightSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
    private readonly RadioButton _showAll = new() { Text = "Show all", AutoSize = true };
    private readonly RadioButton _showChanges = new() { Text = "Show only changes", AutoSize = true };
    private readonly CheckBox _includeSubfolders = new() { Text = "Include subfolders", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label _listTitle = new() { AutoSize = true, Font = UiFonts.Bold, Anchor = AnchorStyles.Left };
    private readonly Label _listSummary = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _leftName = new() { AutoSize = true, Font = UiFonts.Bold };
    private readonly Label _leftDate = new() { AutoSize = true };
    private readonly Label _rightName = new() { AutoSize = true, Font = UiFonts.Bold };
    private readonly Label _rightDate = new() { AutoSize = true };
    private readonly ThemedButton _swap = new() { Text = "Swap", Glyph = Glyphs.Swap, Width = 96 };
    private readonly ThemedButton _refresh = new() { Text = "Refresh", Glyph = Glyphs.Refresh, Width = 104, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly ThemedProgressBar _progress = new() { Dock = DockStyle.Top, Height = 4 };
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _jump = new("Jump to");
    private readonly ToolStripMenuItem _mark = new("Mark as non-important");
    private readonly ToolStripMenuItem _ignore = new("Add to Global Ignore list…");
    private readonly ToolStripMenuItem _exportReg = new("Export to .REG…");
    private readonly ToolStripMenuItem _copy = new("Copy to .\\Files");
    private readonly ToolStripMenuItem _revert = new("Revert Value…");
    private readonly ImportanceMarks _marks = new();
    private readonly List<string> _sessionRules = [];
    private readonly string? _currentSid = WindowsIdentity.GetCurrent().User?.Value;
    private readonly Dictionary<DiffArea, List<GridColumn>> _columns = [];
    private readonly Dictionary<DiffArea, (int Column, bool Descending)> _sort = [];
    private Snapshot? _older;
    private Snapshot? _newer;
    private IgnoreListState? _ignoreState;
    private DiffResult? _diff;
    private ComparisonData? _data;
    private VirtualTreeGrid _active;
    private long? _listedFolder;
    private bool _swapped;
    private bool _loading;
    private bool _building;

    public ComparisonForm(AppPaths paths, AppSettings settings, string olderPath, string newerPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        _leftPath = olderPath;
        _rightPath = newerPath;
        _active = _tree;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        Icon = AppIcon.Value;
        // Window size, not client size: no window is taller than 580 px at 100 % scaling (F14).
        Size = new Size(1180, 580);
        MinimumSize = new Size(820, 460);
        WindowLayout.Remember(this, "Comparison", paths, settings);
        StartPosition = FormStartPosition.CenterParent;
        Text = $"Comparison: {Path.GetFileNameWithoutExtension(olderPath)} ⇄ {Path.GetFileNameWithoutExtension(newerPath)}";
        KeyPreview = true;
        BuildLayout();
        BuildMenu();
        ResumeLayout(performLayout: true);
        ThemeManager.Attach(this);
        ThemeManager.Changed += OnThemeChanged;
    }

    /// <summary>The comparison shown (null while loading).</summary>
    internal DiffResult? Diff => _diff;

    /// <summary>The folder and key tree (left).</summary>
    internal VirtualTreeGrid Tree => _tree;

    /// <summary>The files, values or services of the selected folder or key (right).</summary>
    internal VirtualTreeGrid List => _list;

    internal DetailsPanel Details => _details;

    internal ImportanceMarks Marks => _marks;

    internal bool IsLoading => _loading;

    /// <summary>Show all (true) or Show only changes (false); remembered in the settings.</summary>
    internal bool ShowAll
    {
        get => _showAll.Checked;
        set
        {
            if (value)
            {
                _showAll.Checked = true;
            }
            else
            {
                _showChanges.Checked = true;
            }
        }
    }

    /// <summary>Include subfolders; remembered in the settings.</summary>
    internal bool IncludeSubfolders
    {
        get => _includeSubfolders.Checked;
        set => _includeSubfolders.Checked = value;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // Sizes only once the window has its real size: a split container rejects them before.
            _split.SplitterDistance = Math.Min(380, Math.Max(240, ClientSize.Width / 3));
            _rightSplit.SplitterDistance = Math.Max(80, _rightSplit.Height - DetailsHeight - _rightSplit.SplitterWidth);
            _split.Panel1MinSize = 200;
            _rightSplit.Panel2MinSize = 60;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warning("The comparison window's panes could not be sized", ex);
        }

        await LoadAsync(reloadFiles: true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ThemeManager.Changed -= OnThemeChanged;
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                _ = LoadAsync(reloadFiles: false);
                return true;
            case Keys.Control | Keys.W:
            case Keys.Escape:
                Close();
                return true;
            case Keys.Apps:
            case Keys.Shift | Keys.F10:
                ShowMenuAtFocus();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static string Stamp(SnapshotMeta meta) =>
        meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " · " + meta.ComputerName;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ThemeManager.Post(this, () =>
        {
            // Status colours come from the palette: the models read them as rows are painted.
            _tree.Invalidate();
            _list.Invalidate();
            UpdateDetails();
        });
    }

    // ---- Layout ----

    private void BuildLayout()
    {
        // Top bar: snapshot 1 · ⇄ · snapshot 2 · Refresh.
        var bar = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(16, 10, 16, 10) };
        ThemeManager.SetRole(bar, ThemeRole.Surface);
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(16, 10) };
        ThemeManager.SetRole(left, ThemeRole.Surface);
        left.Controls.Add(_leftName);
        left.Controls.Add(_leftDate);
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        ThemeManager.SetRole(right, ThemeRole.Surface);
        right.Controls.Add(_rightName);
        right.Controls.Add(_rightDate);
        ThemeManager.SetRole(_leftDate, ThemeRole.Secondary);
        ThemeManager.SetRole(_rightDate, ThemeRole.Secondary);
        bar.Controls.Add(left);
        bar.Controls.Add(_swap);
        bar.Controls.Add(right);
        bar.Controls.Add(_refresh);
        void Arrange()
        {
            _swap.Location = new Point(Math.Max(left.Right + 16, 300), 16);
            right.Location = new Point(_swap.Right + 16, 10);
            _refresh.Location = new Point(bar.ClientSize.Width - bar.Padding.Right - _refresh.Width, 16);
        }

        bar.Resize += (_, _) => Arrange();
        left.SizeChanged += (_, _) => Arrange();
        _swap.Click += (_, _) => Swap();
        _refresh.Click += async (_, _) => await LoadAsync(reloadFiles: true);

        var progressHolder = new Panel { Dock = DockStyle.Top, Height = 4 };
        progressHolder.Controls.Add(_progress);

        // Left: Show all / Show only changes, then the tree.
        var modes = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(10, 8, 10, 6) };
        _showAll.Margin = new Padding(0, 0, 20, 0);
        _showChanges.Margin = new Padding(0);
        modes.Controls.Add(_showAll);
        modes.Controls.Add(_showChanges);
        (_settings.ComparisonShowAll ? _showAll : _showChanges).Checked = true;
        _showAll.CheckedChanged += (_, _) =>
        {
            if (_showAll.Checked)
            {
                OnShowAllChanged();
            }
        };
        _showChanges.CheckedChanged += (_, _) =>
        {
            if (_showChanges.Checked)
            {
                OnShowAllChanged();
            }
        };

        _tree.Columns.Add(new GridColumn("Name", 300));
        _tree.SelectionChanged += (_, _) => OnTreeSelectionChanged();
        _tree.ShowToolTips = false;
        _tree.RowActivated += (_, id) => JumpTo(_tree, id);
        _tree.Enter += (_, _) => Activate(_tree);
        _tree.MouseUp += (_, e) => ShowMenuOnRightClick(_tree, e);
        var treeHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 2, 0, 0) };
        treeHolder.Controls.Add(_tree);
        _split.Panel1.Controls.Add(treeHolder);
        _split.Panel1.Controls.Add(new Separator { Dock = DockStyle.Top });
        _split.Panel1.Controls.Add(modes);

        // Right: the selected folder and Include subfolders, the list, the details.
        var listBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 0, 12, 0) };
        listBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _listTitle.Margin = new Padding(0, 0, 12, 0);
        _listTitle.MaximumSize = new Size(520, 0);
        _listTitle.AutoEllipsis = true;
        ThemeManager.SetRole(_listSummary, ThemeRole.Secondary);
        listBar.Controls.Add(_listTitle, 0, 0);
        listBar.Controls.Add(_listSummary, 1, 0);
        listBar.Controls.Add(_includeSubfolders, 2, 0);
        _includeSubfolders.Checked = _settings.ComparisonIncludeSubfolders;
        _includeSubfolders.CheckedChanged += (_, _) =>
        {
            _settings.ComparisonIncludeSubfolders = _includeSubfolders.Checked;
            SaveSettings();
            if (_tree.FocusedId is { } id && !_loading)
            {
                ShowList(id, keepSelection: true);
            }
        };

        _list.TreeColumn = ComparisonListModel.NameColumn;
        _list.SelectionChanged += (_, _) =>
        {
            if (!_building)
            {
                UpdateDetails();
                UpdateStatus();
            }
        };
        _list.ShowToolTips = false;
        _list.RowActivated += (_, id) => JumpTo(_list, id);
        _list.Enter += (_, _) => Activate(_list);
        _list.MouseUp += (_, e) => ShowMenuOnRightClick(_list, e);
        _list.HeaderClicked += (_, column) => SortList(column);
        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 0, 0) };
        listHolder.Controls.Add(_list);
        _rightSplit.Panel1.Controls.Add(listHolder);
        _rightSplit.Panel1.Controls.Add(new Separator { Dock = DockStyle.Top });
        _rightSplit.Panel1.Controls.Add(listBar);
        _rightSplit.Panel2.Controls.Add(_details);
        _split.Panel2.Controls.Add(_rightSplit);

        // Status bar: the selected item's path and the list's state, then the colour legend.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(16, 0, 16, 2) };
        ThemeManager.SetRole(_status, ThemeRole.Secondary);
        footer.Controls.Add(_status);
        footer.Controls.Add(_legend);
        footer.Controls.Add(new Separator { Dock = DockStyle.Top });

        Controls.Add(_split);
        Controls.Add(footer);
        Controls.Add(progressHolder);
        Controls.Add(bar);
    }

    private void BuildMenu()
    {
        _jump.Click += (_, _) =>
        {
            if (_active.SelectedIds.Count == 1)
            {
                JumpTo(_active, _active.SelectedIds.First());
            }
        };
        _mark.Click += (_, _) => ToggleMarks();
        _ignore.Click += async (_, _) => await AddToIgnoreListAsync();
        _exportReg.Click += (_, _) => ExportRegistry();
        _copy.Click += (_, _) => CopyToFiles();
        _revert.Click += (_, _) => RevertValues();
        _menu.Items.AddRange([_jump, new ToolStripSeparator(), _mark, _ignore, new ToolStripSeparator(), _exportReg, _copy, new ToolStripSeparator(), _revert]);
        _menu.Opening += (_, e) =>
        {
            var items = SelectedItems(_active);
            e.Cancel = _diff is null || items.Count == 0;
            if (e.Cancel)
            {
                return;
            }

            var single = items.Count == 1 ? items[0] : (ViewItem?)null;
            _jump.Text = single is { IsCategory: false } one ? one.Area switch
            {
                DiffArea.FileSystem => "Jump to (Explorer)",
                DiffArea.Registry => "Jump to (Regedit)",
                _ => "Jump to (Services)",
            } : "Jump to";
            _jump.Enabled = single is { IsCategory: false };

            // Unchanged items and what a whole new or deleted folder or key holds are for information (F10, F11):
            // only Jump to works on them; the other actions work on changes.
            var selection = Selection(_active);
            var extra = items.Any(i => ComparisonData.ItemOf(i) is null);
            _mark.Enabled = !extra && DiffActions.IsEnabled(DiffAction.MarkNonImportant, selection);
            _mark.Checked = !extra && selection.Count > 0 && selection.All(_marks.IsMarked);
            _ignore.Enabled = !extra && DiffActions.IsEnabled(DiffAction.AddToIgnoreList, selection);
            _exportReg.Enabled = !extra && DiffActions.IsEnabled(DiffAction.ExportReg, selection);
            _copy.Enabled = !extra && DiffActions.IsEnabled(DiffAction.CopyToFiles, selection);
            _revert.Enabled = !extra && DiffActions.IsEnabled(DiffAction.RevertValue, selection);
            ThemeManager.ApplyMenu(_menu, ThemeManager.Current);
        };
    }

    private void Activate(VirtualTreeGrid grid)
    {
        if (_active == grid)
        {
            return;
        }

        _active = grid;
        UpdateDetails();
        UpdateStatus();
    }

    private void ShowMenuOnRightClick(VirtualTreeGrid grid, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && grid.SelectedIds.Count > 0 && e.Y > grid.HeaderHeight)
        {
            _active = grid;
            _menu.Show(grid, e.Location);
        }
    }

    private void ShowMenuAtFocus()
    {
        if (_active.SelectedIds.Count > 0)
        {
            _menu.Show(_active, new Point(40, _active.HeaderHeight + 8));
        }
    }

    /// <summary>The items selected in a pane.</summary>
    private List<ViewItem> SelectedItems(VirtualTreeGrid grid) => grid.Model switch
    {
        ComparisonTreeModel tree => [.. grid.SelectedIds.Select(tree.ItemOf)],
        ComparisonListModel list => [.. grid.SelectedIds.Select(list.ItemOf)],
        _ => [],
    };

    /// <summary>The comparison items selected in a pane, for the actions; items for information only are left out.</summary>
    private List<DiffItem> Selection(VirtualTreeGrid grid) =>
        [.. SelectedItems(grid).Select(ComparisonData.ItemOf).OfType<DiffItem>()];

    // ---- Loading and comparing ----

    /// <summary>Loads both snapshots (when asked) and compares them with the Global Ignore list in effect.</summary>
    internal async Task LoadAsync(bool reloadFiles)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _refresh.Enabled = false;
        _swap.Enabled = false;
        _progress.Marquee = true;
        _progress.Visible = true;
        var watch = Stopwatch.StartNew();
        try
        {
            if (reloadFiles || _older is null || _newer is null)
            {
                _status.Text = "Loading the snapshots…";
                var leftTask = Task.Run(() => SnapshotReader.Load(_leftPath));
                var rightTask = Task.Run(() => SnapshotReader.Load(_rightPath));
                var a = await leftTask;
                var b = await rightTask;
                a.Meta.Name = Path.GetFileNameWithoutExtension(_leftPath);
                b.Meta.Name = Path.GetFileNameWithoutExtension(_rightPath);
                (_older, _newer) = a.Meta.StartedAt <= b.Meta.StartedAt ? (a, b) : (b, a);
            }

            _status.Text = "Reading the Global Ignore list…";
            _ignoreState = await LoadIgnoreListAsync();
            var rules = _ignoreState.List.Rules.Select(r => r.Text).Concat(_sessionRules);
            var matcher = _sessionRules.Count == 0 ? _ignoreState.List.Matcher : GlobalIgnoreList.Build(rules).Matcher;

            _status.Text = "Comparing…";
            var older = _older!;
            var newer = _newer!;
            var diff = await Task.Run(() => SnapshotComparer.Compare(older, newer, matcher));

            _status.Text = "Building the view…";
            var view = CaptureView();
            _diff = _swapped ? diff.Swap() : diff;
            ShowDiff(view);
            Log.Info($"Compared {older.Meta.Name} and {newer.Meta.Name} in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
        {
            _status.Text = "The snapshots could not be read: " + FriendlyErrors.Describe(ex);
            MessageDialog.Error(
                this,
                _diff is null
                    ? "The snapshots could not be compared."
                    : "Refresh failed. The comparison shown is still the one loaded before.",
                ex);
        }
        finally
        {
            _loading = false;
            _progress.Marquee = false;
            _progress.Visible = false;
            _refresh.Enabled = true;
            _swap.Enabled = _diff is not null;
        }

        UpdateDetails();
        UpdateStatus();
    }

    private async Task<IgnoreListState> LoadIgnoreListAsync()
    {
        try
        {
            using var http = new HttpClient();
            return await IgnoreListLoader.LoadAsync(_settings.IgnoreListSource, _settings.IgnoreListRemoteLocation, new LocalIgnoreFile(_paths.LocalIgnoreFile), http);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Log.Warning("Global Ignore list could not be read", ex);
            return new IgnoreListState(GlobalIgnoreList.Empty, IgnoreListOrigin.Local, "No Global Ignore list could be read.");
        }
    }

    /// <summary>Shows <see cref="_diff"/>: the tree with the categories open, or as <paramref name="view"/> had it.</summary>
    private void ShowDiff(ViewState? view)
    {
        if (_diff is null)
        {
            return;
        }

        _leftName.Text = _diff.Left.Meta.Name;
        _leftDate.Text = Stamp(_diff.Left.Meta);
        _rightName.Text = _diff.Right.Meta.Name;
        _rightDate.Text = Stamp(_diff.Right.Meta);
        Text = $"Comparison: {_diff.Left.Meta.Name} ⇄ {_diff.Right.Meta.Name}";
        _data = new ComparisonData(_diff, _marks, _currentSid, _showAll.Checked);
        var model = new ComparisonTreeModel(_data);
        _building = true;
        try
        {
            _listedFolder = null;
            _tree.Model = model;
            if (view is null)
            {
                foreach (var category in model.Roots)
                {
                    _tree.Expand(category);
                }

                _tree.SetSelection([model.Roots[0]]);
            }
            else
            {
                RestoreView(view);
            }
        }
        finally
        {
            _building = false;
        }

        if (_tree.FocusedId is { } focused)
        {
            ShowList(focused, keepSelection: view is not null, view?.SelectedList, view?.ListTop ?? 0);
        }
    }

    private void OnShowAllChanged()
    {
        _settings.ComparisonShowAll = _showAll.Checked;
        SaveSettings();
        if (_diff is null || _loading)
        {
            return;
        }

        ShowDiff(CaptureView());
        UpdateDetails();
        UpdateStatus();
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

    // ---- The list ----

    private void OnTreeSelectionChanged()
    {
        if (_building || _diff is null)
        {
            return;
        }

        if (_tree.SelectedIds.Count == 1 && _tree.FocusedId is { } id && id != _listedFolder)
        {
            ShowList(id, keepSelection: false);
        }

        UpdateDetails();
        UpdateStatus();
    }

    /// <summary>Lists the files, values or services of a folder, key or category, sorted as last chosen for its kind.</summary>
    private void ShowList(long folderId, bool keepSelection, HashSet<string>? selectedKeys = null, int top = 0)
    {
        if (_data is null || _tree.Model is not ComparisonTreeModel tree)
        {
            return;
        }

        var folder = tree.ItemOf(folderId);
        if (keepSelection && selectedKeys is null && _list.Model is ComparisonListModel previous)
        {
            selectedKeys = [.. _list.SelectedIds.Select(previous.RowKey)];
            top = _list.TopRow;
        }

        var model = new ComparisonListModel(_data, folder, _includeSubfolders.Checked);
        var (column, descending) = _sort.TryGetValue(folder.Area, out var sort) ? sort : (ComparisonListModel.NameColumn, false);
        model.Sort(column, descending);
        if (!_columns.TryGetValue(folder.Area, out var columns))
        {
            columns = ComparisonListModel.ColumnsFor(folder.Area);
            _columns[folder.Area] = columns;
        }

        _building = true;
        try
        {
            _listedFolder = folderId;
            _list.Columns.Clear();
            _list.Columns.AddRange(columns);
            _list.SortColumn = model.SortColumn;
            _list.SortDescending = model.SortDescending;
            _list.Model = model;
            if (selectedKeys is { Count: > 0 })
            {
                _list.SetSelection(model.Roots.Where(id => selectedKeys.Contains(model.RowKey(id))).ToList());
            }

            _list.ScrollTo(top);
        }
        finally
        {
            _building = false;
        }

        _listTitle.Text = folder.IsCategory ? ComparisonData.CategoryName(folder.Area) : _data.Path(folder);
        _listSummary.Text = ListSummary(model);
    }

    private static string ListSummary(ComparisonListModel model)
    {
        if (model.Folder.IsCategory && model.Area != DiffArea.Services)
        {
            return model.Area == DiffArea.FileSystem ? "Select a drive or folder on the left." : "Select a key on the left.";
        }

        long added = 0, modified = 0, deleted = 0;
        foreach (var id in model.Roots)
        {
            switch (model.ItemOf(id).Status)
            {
                case RowStatus.New:
                    added++;
                    break;
                case RowStatus.Modified:
                    modified++;
                    break;
                case RowStatus.Deleted:
                    deleted++;
                    break;
            }
        }

        var noun = model.Area switch
        {
            DiffArea.FileSystem => "file",
            DiffArea.Registry => "value",
            _ => "service",
        };
        var text = DiffItems.Count(model.Count, noun) + string.Create(CultureInfo.CurrentCulture, $" · {added:N0} new, {modified:N0} modified, {deleted:N0} deleted");
        return model.Truncated ? text + string.Create(CultureInfo.CurrentCulture, $" · only the first {ComparisonListModel.MaxRows:N0} are listed") : text;
    }

    /// <summary>A header click sorts by that column; a second click on it turns the order round.</summary>
    internal void SortList(int column)
    {
        if (_list.Model is not ComparisonListModel model)
        {
            return;
        }

        var descending = column == model.SortColumn && !model.SortDescending;
        model.Sort(column, descending);
        _sort[model.Area] = (column, descending);
        _list.SortColumn = column;
        _list.SortDescending = descending;
        _list.Rebuild(keepState: true);
    }

    // ---- Details and status bar ----

    private void UpdateDetails()
    {
        if (_data is null || _diff is null)
        {
            return;
        }

        var items = SelectedItems(_active);
        if (items.Count == 0 && _active == _list)
        {
            items = SelectedItems(_tree);
        }

        if (items.Count != 1)
        {
            _details.ShowMessage(items.Count > 1
                ? string.Create(CultureInfo.CurrentCulture, $"{items.Count:N0} items selected.")
                : "Select a folder, file, key, value or service to see it in both snapshots.");
            return;
        }

        var item = items[0];
        if (item.IsCategory)
        {
            _details.ShowMessage(_data.CategoryText(item.Area));
            return;
        }

        var name = _active == _list && _list.Model is ComparisonListModel list && _list.SelectedIds.Count == 1
            ? list.Text(_list.SelectedIds.First(), ComparisonListModel.NameColumn)
            : _data.DisplayName(item);
        var status = item.Status switch
        {
            RowStatus.New => "New",
            RowStatus.Deleted => "Deleted",
            RowStatus.Modified => "Modified",
            RowStatus.Inside => "Changes inside",
            _ => "Unchanged",
        };
        _details.ShowItem(name, status, ComparisonData.TextColor(item.Status), SelectedPath() ?? string.Empty, _diff.Left.Meta.Name, _diff.Right.Meta.Name, _data.Details(item));
    }

    /// <summary>The full path of the one selected item in the active pane, or null.</summary>
    private string? SelectedPath()
    {
        if (_data is null || _active.SelectedIds.Count != 1)
        {
            return null;
        }

        var id = _active.SelectedIds.First();
        return _active.Model switch
        {
            ComparisonListModel list => list.PathOf(id),
            ComparisonTreeModel tree => _data.Path(tree.ItemOf(id)),
            _ => null,
        };
    }

    private void UpdateStatus()
    {
        if (_diff is null || _loading)
        {
            return;
        }

        var parts = new List<string>();
        if (_active.SelectedIds.Count > 1)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_active.SelectedIds.Count:N0} selected"));
        }
        else if (SelectedPath() is { } path)
        {
            // The full path of the one selected item, in place of hover tool tips (F19).
            parts.Add(path);
        }

        if (_marks.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_marks.Count:N0} marked as non-important"));
        }

        if (_ignoreState is { } state)
        {
            var origin = state.Origin switch
            {
                IgnoreListOrigin.Remote => "local + remote",
                IgnoreListOrigin.LocalFallback => "local only (remote not reachable)",
                _ => "local only",
            };
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"Global Ignore list: {origin}, {state.List.Rules.Count + _sessionRules.Count:N0} rules"));
        }

        if (SnapshotMeta.RightsWarning(_diff.Older.Meta, _diff.Newer.Meta) is { } rights)
        {
            parts.Insert(0, "⚠ " + rights);
        }

        parts.Add("Right-click for actions");
        _status.Text = string.Join("  ·  ", parts);
    }

    // ---- Keeping the view (F18) ----

    /// <summary>Open folders, the selected folder and items, the scroll positions and the marks, by path.</summary>
    private sealed record ViewState(HashSet<string> Expanded, string? SelectedTree, int TreeTop, HashSet<string> SelectedList, int ListTop, HashSet<string> Marked, bool ListActive);

    private ViewState? CaptureView()
    {
        if (_tree.Model is not ComparisonTreeModel tree || _diff is null)
        {
            return null;
        }

        var rows = _tree.VisibleIds;
        var expanded = rows.Where(_tree.IsExpanded).Select(tree.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedTree = _tree.FocusedId is { } focused ? tree.RowKey(focused) : null;
        var selectedList = _list.Model is ComparisonListModel list
            ? _list.SelectedIds.Select(list.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var marked = _marks.Items.Select(i => MarkKey(_diff, i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ViewState(expanded, selectedTree, _tree.TopRow, selectedList, _list.TopRow, marked, _active == _list);
    }

    private static string MarkKey(DiffResult diff, DiffItem item) => $"{(int)item.Area}|{diff.FullPath(diff.Tree(item.Area), item.Node)}";

    /// <summary>
    /// After comparing again (Refresh, a new Global Ignore rule, Swap, Show all), opens the same folders and keys,
    /// selects the same folder and items and scrolls back, so the view does not collapse (F18). Items that are
    /// now ignored are simply gone.
    /// </summary>
    private void RestoreView(ViewState view)
    {
        if (_tree.Model is not ComparisonTreeModel tree || _diff is null)
        {
            return;
        }

        var marks = new List<DiffItem>();
        if (view.Marked.Count > 0)
        {
            foreach (var area in new[] { DiffArea.FileSystem, DiffArea.Registry, DiffArea.Services })
            {
                var diffTree = _diff.Tree(area);
                for (var n = 0; n < diffTree.Count; n++)
                {
                    if (view.Marked.Contains(MarkKey(_diff, new DiffItem(area, n))))
                    {
                        marks.Add(new DiffItem(area, n));
                    }
                }
            }
        }

        _marks.Clear();
        _marks.Toggle(marks);

        // Rows are visited top to bottom; expanding one inserts its children right below it, so they are visited next.
        var rows = _tree.VisibleIds;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i];
            if (view.Expanded.Contains(tree.RowKey(id)) && tree.HasChildren(id))
            {
                _tree.Expand(id);
            }
        }

        var selected = view.SelectedTree is null ? -1 : rows.ToList().FindIndex(id => string.Equals(tree.RowKey(id), view.SelectedTree, StringComparison.OrdinalIgnoreCase));
        _tree.SetSelection([rows[selected >= 0 ? selected : 0]]);
        _tree.ScrollTo(view.TreeTop);
        if (view.ListActive)
        {
            _active = _list;
        }
    }

    /// <summary>⇄: snapshot 2 on the left; New and Deleted trade places. No recomparison.</summary>
    internal void Swap()
    {
        if (_diff is null)
        {
            return;
        }

        _swapped = !_swapped;
        var view = CaptureView();
        _diff = _diff.Swap();
        ShowDiff(view);
        UpdateDetails();
        UpdateStatus();
    }

    // ---- Context menu actions (plan §10.6) ----

    /// <summary>Opens an item where it lives: a folder or file in Explorer, a key (a value's key) in Regedit, a service in Services.</summary>
    private void JumpTo(VirtualTreeGrid grid, long id)
    {
        if (_data is null)
        {
            return;
        }

        ViewItem item;
        string path;
        switch (grid.Model)
        {
            case ComparisonTreeModel tree:
                item = tree.ItemOf(id);
                path = item.IsCategory ? string.Empty : _data.Path(item);
                break;
            case ComparisonListModel list:
                item = list.ItemOf(id);
                path = list.JumpPathOf(id);
                break;
            default:
                return;
        }

        if (item.IsCategory)
        {
            return;
        }

        try
        {
            switch (item.Area)
            {
                case DiffArea.FileSystem:
                    ShellLauncher.OpenInExplorer(path);
                    break;
                case DiffArea.Registry:
                    ShellLauncher.OpenInRegedit(path);
                    break;
                default:
                    var (snapshot, index) = _data.Shown(item);
                    var display = snapshot.Names.GetString(snapshot.Services.DisplayName[index]);
                    _ = JumpToServiceAsync(string.IsNullOrEmpty(display) ? _data.Name(item) : display);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageDialog.Error(this, "The item could not be opened.", ex);
        }
    }

    private async Task JumpToServiceAsync(string display)
    {
        _status.Text = $"Opening Services and selecting \"{display}\"… (the name is also on the clipboard)";
        var selected = await ServicesConsole.OpenAndSelectAsync(display);
        if (!selected)
        {
            _status.Text = $"Services is open; \"{display}\" is on the clipboard: click the list and type the name to find it.";
        }
    }

    private void ToggleMarks()
    {
        _marks.Toggle(Selection(_active).Where(i => !DiffActions.IsCategory(i)));
        _tree.Invalidate();
        _list.Invalidate();
        UpdateStatus();
    }

    private async Task AddToIgnoreListAsync()
    {
        if (_diff is null)
        {
            return;
        }

        var selection = Selection(_active).Where(i => !DiffActions.IsCategory(i)).ToList();
        var rules = selection.Select(i => DiffItems.IgnoreRule(_diff, i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rules.Count == 0)
        {
            return;
        }

        // The rules can be changed before they are added, e.g. a folder made into C:\Users\*\… (F20).
        var edited = IgnoreRulesDialog.Edit(
            this,
            "Add to Global Ignore list",
            $"Add {(rules.Count == 1 ? "this rule" : $"these {rules.Count} rules")} to the local Global Ignore list? Change them first if needed. "
                + "They are left out of new snapshots and comparisons from now on, and hidden here now.",
            rules,
            "Add");
        if (edited is null)
        {
            return;
        }

        rules = [.. edited.Distinct(StringComparer.OrdinalIgnoreCase)];

        try
        {
            var local = new LocalIgnoreFile(_paths.LocalIgnoreFile);
            foreach (var rule in rules)
            {
                local.AddRule(rule);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "The rules could not be saved.", ex);
            return;
        }

        _sessionRules.AddRange(rules);
        await LoadAsync(reloadFiles: false);
    }

    private void ExportRegistry()
    {
        if (_diff is null)
        {
            return;
        }

        var nodes = DiffActions.Nodes(_diff, Selection(_active), DiffArea.Registry);
        var (file, notStored) = DiffExport.Registry(_diff, nodes, _marks);
        if (file.KeyCount == 0)
        {
            MessageDialog.Inform(this, "Export to .REG", "Nothing new or modified to export in the selection. Deleted keys and values are not exported.");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Registration files (*.reg)|*.reg",
            InitialDirectory = _paths.DataDirectory,
            FileName = SafeFileName($"Export_{_diff.Left.Meta.Name}_vs_{_diff.Right.Meta.Name}.reg"),
            Title = "Export to .REG",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            file.Save(dialog.FileName);
            Log.Info($"Exported {file.KeyCount} keys and {file.ValueCount} values to {dialog.FileName}");
            var message = string.Create(CultureInfo.CurrentCulture, $"{file.KeyCount:N0} keys and {file.ValueCount:N0} values from \"{_diff.Newer.Meta.Name}\" were written to {dialog.FileName}.");
            MessageDialog.Inform(
                this,
                "Export to .REG",
                notStored.Count == 0 ? message : message + " These values are larger than 1 MB, are not in the snapshot, and were left out:",
                notStored.Count == 0 ? null : notStored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "The file could not be written.", ex);
        }
    }

    private void CopyToFiles()
    {
        if (_diff is null)
        {
            return;
        }

        var nodes = DiffActions.Nodes(_diff, Selection(_active), DiffArea.FileSystem);
        var files = DiffExport.Files(_diff, nodes, _marks);
        if (files.Count == 0)
        {
            MessageDialog.Inform(this, "Copy to .\\Files", "There are no new or modified files in the selection. Deleted files cannot be copied.");
            return;
        }

        using var window = new CopyProgressForm(files, _paths.FilesDirectory);
        window.ShowDialog(this);
    }

    private void RevertValues()
    {
        if (_diff is null)
        {
            return;
        }

        var nodes = DiffActions.Nodes(_diff, Selection(_active), DiffArea.Registry);
        var items = RegistryReverter.Plan(_diff, nodes);
        if (items.Count == 0)
        {
            MessageDialog.Inform(this, "Revert Value", "There are no changed values directly in the selected keys.");
            return;
        }

        var target = RegistryReverter.Target(_diff);
        var details = items.Select(item =>
        {
            var current = RegistryReverter.ReadCurrent(item);
            var now = current.Exists ? $"{ItemFormat.ValueType(current.Type)} {ItemFormat.ValueData(current.Type, current.Data)}" : "(does not exist)";
            var restored = item.Problem is { } problem ? $"cannot be reverted: {problem}"
                : item.Action == RevertAction.Delete ? "(deleted)"
                : $"{ItemFormat.ValueType(item.Type)} {ItemFormat.ValueData(item.Type, item.Data)}";
            return $"{item.DisplayPath}:  {now}  →  {restored}";
        }).ToList();

        if (!MessageDialog.Ask(this, "Revert Value", $"Do you want to revert to value from Snapshot {target.Meta.Name}? A backup of the current values is saved in {_paths.BackupsDirectory} first.", "Revert", "Cancel", details))
        {
            return;
        }

        try
        {
            var result = RegistryReverter.Apply(items, _paths.BackupsDirectory, target.Meta.Name);
            var failed = result.Outcomes.Where(o => o.Error is not null).Select(o => $"{o.Item.DisplayPath}: {o.Error}").ToList();
            Log.Info($"Reverted {result.Succeeded} of {result.Outcomes.Count} values to {target.Meta.Name}; backup {result.BackupPath}");
            MessageDialog.Inform(
                this,
                "Revert Value",
                string.Create(CultureInfo.CurrentCulture, $"{result.Succeeded:N0} of {result.Outcomes.Count:N0} values were reverted. To undo, import the backup {result.BackupPath}.") +
                (failed.Count > 0 ? " These could not be reverted:" : string.Empty),
                failed.Count > 0 ? failed : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "The backup could not be written, so nothing was changed.", ex);
        }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
