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
/// W4 Snapshot Comparison (plan §10.4, Steps 51–52): top bar with ⇄ and Refresh, a progress bar while loading,
/// the three categories, only changed items and their parents, and the context menu of §10.6.
/// </summary>
internal sealed class ComparisonForm : Form
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly string _leftPath;
    private readonly string _rightPath;
    private readonly VirtualTreeGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly Label _leftName = new() { AutoSize = true, Font = UiFonts.Bold };
    private readonly Label _leftDate = new() { AutoSize = true };
    private readonly Label _rightName = new() { AutoSize = true, Font = UiFonts.Bold };
    private readonly Label _rightDate = new() { AutoSize = true };
    private readonly ThemedButton _swap = new() { Text = "Swap", Glyph = Glyphs.Swap, Width = 96 };
    private readonly ThemedButton _refresh = new() { Text = "Refresh", Glyph = Glyphs.Refresh, Width = 104, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly CheckBox _showUnchanged = new() { Text = "Show unchanged", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
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
    private Snapshot? _older;
    private Snapshot? _newer;
    private IgnoreListState? _ignoreState;
    private DiffResult? _diff;
    private bool _swapped;
    private bool _loading;

    public ComparisonForm(AppPaths paths, AppSettings settings, string olderPath, string newerPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        _leftPath = olderPath;
        _rightPath = newerPath;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        Icon = AppIcon.Value;
        // Window size, not client size: no window is taller than 580 px at 100 % scaling (F14).
        Size = new Size(1180, 580);
        MinimumSize = new Size(760, 420);
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

    internal VirtualTreeGrid Grid => _grid;

    internal ImportanceMarks Marks => _marks;

    /// <summary>"Show unchanged" (issue #4): off when the window opens.</summary>
    internal bool ShowUnchanged
    {
        get => _showUnchanged.Checked;
        set => _showUnchanged.Checked = value;
    }

    private ComparisonModel NewModel(DiffResult diff) => new(diff, _marks, _currentSid) { ShowUnchanged = _showUnchanged.Checked };

    internal bool IsLoading => _loading;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
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
        ThemeManager.Post(this, _grid.Invalidate);
    }

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
        bar.Controls.Add(_showUnchanged);
        void Arrange()
        {
            _swap.Location = new Point(Math.Max(left.Right + 16, 300), 16);
            right.Location = new Point(_swap.Right + 16, 10);
            _refresh.Location = new Point(bar.ClientSize.Width - bar.Padding.Right - _refresh.Width, 16);
            _showUnchanged.Location = new Point(_refresh.Left - _showUnchanged.Width - 20, 22);
        }

        // Unchanged items are listed inside what is opened; the open folders, selection and scroll stay.
        _showUnchanged.CheckedChanged += (_, _) =>
        {
            if (_diff is null || _loading)
            {
                return;
            }

            var view = CaptureView();
            ShowDiff(keepState: false);
            if (view is not null)
            {
                RestoreView(view);
            }

            UpdateStatus();
        };

        bar.Resize += (_, _) => Arrange();
        left.SizeChanged += (_, _) => Arrange();
        _swap.Click += (_, _) => Swap();
        _refresh.Click += async (_, _) => await LoadAsync(reloadFiles: true);

        var progressHolder = new Panel { Dock = DockStyle.Top, Height = 4 };
        progressHolder.Controls.Add(_progress);

        _grid.Columns.Add(new GridColumn(string.Empty, 28, HorizontalAlignment.Left));
        _grid.Columns.Add(new GridColumn("Name", 460));
        _grid.TreeColumn = ComparisonModel.NameColumn;
        _grid.Columns.Add(new GridColumn("Snapshot 1", 320));
        _grid.Columns.Add(new GridColumn("Snapshot 2", 320));
        _grid.SelectionChanged += (_, _) => UpdateStatus();
        _grid.ShowToolTips = false; // F19: the status bar shows the selected item's full path instead
        _grid.RowActivated += (_, id) =>
        {
            if (ComparisonModel.IsExtra(id))
            {
                JumpExtra(id);
            }
            else
            {
                JumpTo([ComparisonModel.Item(id)]);
            }
        };
        _grid.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _grid.SelectedIds.Count > 0 && e.Y > _grid.HeaderHeight)
            {
                _menu.Show(_grid, e.Location);
            }
        };

        var gridHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 0, 0) };
        gridHolder.Controls.Add(_grid);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(16, 0, 16, 2) };
        ThemeManager.SetRole(_status, ThemeRole.Secondary);
        footer.Controls.Add(_status);
        footer.Controls.Add(new Separator { Dock = DockStyle.Top });

        Controls.Add(gridHolder);
        Controls.Add(footer);
        Controls.Add(progressHolder);
        Controls.Add(bar);
    }

    private void BuildMenu()
    {
        _jump.Click += (_, _) =>
        {
            if (ExtraSelected() is { } extra)
            {
                JumpExtra(extra);
            }
            else
            {
                JumpTo(Selection());
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
            var selection = Selection();
            e.Cancel = _diff is null || _grid.SelectedIds.Count == 0;
            if (_grid.SelectedIds.Any(ComparisonModel.IsExtra))
            {
                // What a whole new or deleted folder or key holds is shown for information (F10, F11);
                // the actions work on that folder's or key's own row.
                foreach (var item in new[] { _mark, _ignore, _exportReg, _copy, _revert })
                {
                    item.Enabled = false;
                }

                _mark.Checked = false;
                _jump.Enabled = ExtraSelected() is not null;
                _jump.Text = ExtraSelected() is { } one
                    ? ComparisonModel.ExtraArea(one) switch
                    {
                        DiffArea.FileSystem => "Jump to (Explorer)",
                        DiffArea.Registry => "Jump to (Regedit)",
                        _ => "Jump to (Services)",
                    }
                    : "Jump to";
                ThemeManager.ApplyMenu(_menu, ThemeManager.Current);
                return;
            }

            _jump.Enabled = DiffActions.IsEnabled(DiffAction.JumpTo, selection);
            _jump.Text = selection.Count == 1 ? selection[0].Area switch
            {
                DiffArea.FileSystem => "Jump to (Explorer)",
                DiffArea.Registry => "Jump to (Regedit)",
                _ => "Jump to (Services)",
            } : "Jump to";
            _mark.Enabled = DiffActions.IsEnabled(DiffAction.MarkNonImportant, selection);
            _mark.Checked = selection.Count > 0 && selection.All(_marks.IsMarked);
            _ignore.Enabled = DiffActions.IsEnabled(DiffAction.AddToIgnoreList, selection);
            _exportReg.Enabled = DiffActions.IsEnabled(DiffAction.ExportReg, selection);
            _copy.Enabled = DiffActions.IsEnabled(DiffAction.CopyToFiles, selection);
            _revert.Enabled = DiffActions.IsEnabled(DiffAction.RevertValue, selection);
            ThemeManager.ApplyMenu(_menu, ThemeManager.Current);
        };
    }

    private List<DiffItem> Selection() => ComparisonModel.Items(_grid.SelectedIds.Where(id => !ComparisonModel.IsExtra(id)));

    /// <summary>The selected row when it is the only one and is for information only (inside a new folder, or unchanged).</summary>
    private long? ExtraSelected() =>
        _grid.SelectedIds.Count == 1 && _grid.SelectedIds.First() is var id && ComparisonModel.IsExtra(id) ? id : null;

    private void JumpExtra(long id)
    {
        if (_grid.Model is not ComparisonModel model)
        {
            return;
        }

        try
        {
            switch (ComparisonModel.ExtraArea(id))
            {
                case DiffArea.FileSystem:
                    ShellLauncher.OpenInExplorer(model.JumpPath(id));
                    break;
                case DiffArea.Registry:
                    ShellLauncher.OpenInRegedit(model.JumpPath(id));
                    break;
                default:
                    var display = model.Text(id, ComparisonModel.NameColumn);
                    var paren = display.LastIndexOf("  (", StringComparison.Ordinal);
                    _ = JumpToServiceAsync(paren > 0 ? display[..paren] : display);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageDialog.Error(this, "The item could not be opened.", ex);
        }
    }

    private void ShowMenuAtFocus()
    {
        if (_grid.SelectedIds.Count > 0)
        {
            _menu.Show(_grid, new Point(40, _grid.HeaderHeight + 8));
        }
    }

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
            ShowDiff(keepState: false);
            if (view is not null)
            {
                RestoreView(view);
            }

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

    private void ShowDiff(bool keepState)
    {
        if (_diff is null)
        {
            return;
        }

        _leftName.Text = _diff.Left.Meta.Name;
        _leftDate.Text = Stamp(_diff.Left.Meta);
        _rightName.Text = _diff.Right.Meta.Name;
        _rightDate.Text = Stamp(_diff.Right.Meta);
        _grid.Columns[ComparisonModel.LeftColumn].Header = _diff.Left.Meta.Name;
        _grid.Columns[ComparisonModel.RightColumn].Header = _diff.Right.Meta.Name;
        var model = NewModel(_diff);
        if (keepState)
        {
            var expanded = _grid.Model?.Roots.Concat(AllExpanded()).ToList() ?? [];
            _grid.Model = model;
            foreach (var id in expanded)
            {
                _grid.Expand(id);
            }
        }
        else
        {
            _grid.Model = model;
        }

        Text = $"Comparison: {_diff.Left.Meta.Name} ⇄ {_diff.Right.Meta.Name}";
    }

    /// <summary>Expanded and selected rows, the first row on screen, and the marks, by path (F18).</summary>
    private sealed record ViewState(HashSet<string> Expanded, HashSet<string> Selected, string? Top, int TopRow, HashSet<string> Marked);

    private ViewState? CaptureView()
    {
        if (_grid.Model is not ComparisonModel model)
        {
            return null;
        }

        var rows = _grid.VisibleIds;
        var expanded = rows.Where(_grid.IsExpanded).Select(model.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = _grid.SelectedIds.Select(model.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var top = _grid.TopRow < rows.Count ? model.RowKey(rows[_grid.TopRow]) : null;
        var marked = _marks.Items.Select(i => model.RowKey(ComparisonModel.NodeId(i.Area, i.Node))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ViewState(expanded, selected, top, _grid.TopRow, marked);
    }

    /// <summary>
    /// After comparing again, opens the same folders and keys, selects the same rows and scrolls back, so adding
    /// a Global Ignore rule or Refresh does not collapse the tree (F18). Rows that are now ignored are simply gone.
    /// </summary>
    private void RestoreView(ViewState view)
    {
        if (_grid.Model is not ComparisonModel model)
        {
            return;
        }

        var marks = new List<DiffItem>();
        if (view.Marked.Count > 0 && _diff is not null)
        {
            foreach (var area in ComparisonModel.AllAreas)
            {
                var tree = _diff.Tree(area);
                for (var n = 0; n < tree.Count; n++)
                {
                    if (view.Marked.Contains(model.RowKey(ComparisonModel.NodeId(area, n))))
                    {
                        marks.Add(new DiffItem(area, n));
                    }
                }
            }
        }

        _marks.Clear();
        _marks.Toggle(marks);

        // Rows are visited top to bottom; expanding one inserts its children right below it, so they are visited next.
        var rows = _grid.VisibleIds;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i];
            if (view.Expanded.Count > 0 && model.HasChildren(id) && view.Expanded.Contains(model.RowKey(id)))
            {
                _grid.Expand(id);
            }
        }

        if (view.Selected.Count > 0)
        {
            _grid.SetSelection(rows.Where(id => view.Selected.Contains(model.RowKey(id))).ToList());
        }
        var top = -1;
        if (view.Top is not null)
        {
            for (var i = 0; i < rows.Count && top < 0; i++)
            {
                if (string.Equals(model.RowKey(rows[i]), view.Top, StringComparison.OrdinalIgnoreCase))
                {
                    top = i;
                }
            }
        }

        _grid.ScrollTo(top >= 0 ? top : view.TopRow);
    }

    private IEnumerable<long> AllExpanded()
    {
        if (_grid.Model is null)
        {
            yield break;
        }

        var stack = new Stack<long>(_grid.Model.Roots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (_grid.IsExpanded(id))
            {
                yield return id;
                foreach (var child in _grid.Model.Children(id))
                {
                    stack.Push(child);
                }
            }
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
        var expanded = AllExpanded().ToList();
        var selected = _grid.SelectedIds.ToList();
        _diff = _diff.Swap();
        _grid.Model = NewModel(_diff);
        _leftName.Text = _diff.Left.Meta.Name;
        _leftDate.Text = Stamp(_diff.Left.Meta);
        _rightName.Text = _diff.Right.Meta.Name;
        _rightDate.Text = Stamp(_diff.Right.Meta);
        _grid.Columns[ComparisonModel.LeftColumn].Header = _diff.Left.Meta.Name;
        _grid.Columns[ComparisonModel.RightColumn].Header = _diff.Right.Meta.Name;
        foreach (var id in expanded.OrderBy(id => id))
        {
            _grid.Expand(id);
        }

        if (selected.Count > 0)
        {
            _grid.Select(selected[0]);
        }

        Text = $"Comparison: {_diff.Left.Meta.Name} ⇄ {_diff.Right.Meta.Name}";
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_diff is null || _loading)
        {
            return;
        }

        var parts = new List<string>();
        if (_grid.SelectedIds.Count > 1)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{_grid.SelectedIds.Count:N0} selected"));
        }
        else if (_grid.SelectedIds.Count == 1 && _grid.Model is ComparisonModel model
            && model.ToolTip(_grid.SelectedIds.First(), ComparisonModel.NameColumn) is { } path)
        {
            // The full path of the one selected item, in place of the hover tool tips (F19).
            parts.Add(path.Replace(Environment.NewLine, " · ", StringComparison.Ordinal));
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

    // ---- Context menu actions (plan §10.6) ----

    private void JumpTo(IReadOnlyList<DiffItem> selection)
    {
        if (_diff is null || !DiffActions.IsEnabled(DiffAction.JumpTo, selection))
        {
            return;
        }

        var item = selection[0];
        var tree = _diff.Tree(item.Area);
        try
        {
            switch (item.Area)
            {
                case DiffArea.FileSystem:
                    ShellLauncher.OpenInExplorer(_diff.FullPath(tree, item.Node));
                    break;
                case DiffArea.Registry:
                    var node = tree.Kind(item.Node) == DiffItemKind.Value ? tree.Parent(item.Node) : item.Node;
                    ShellLauncher.OpenInRegedit(_diff.FullPath(tree, node));
                    break;
                default:
                    _ = JumpToServiceAsync(item);
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageDialog.Error(this, "The item could not be opened.", ex);
        }
    }

    private Task JumpToServiceAsync(DiffItem item)
    {
        var tree = _diff!.Tree(item.Area);
        var snapshot = tree.Left(item.Node) >= 0 ? _diff.Left : _diff.Right;
        var index = tree.Left(item.Node) >= 0 ? tree.Left(item.Node) : tree.Right(item.Node);
        var display = snapshot.Names.GetString(snapshot.Services.DisplayName[index]);
        return JumpToServiceAsync(string.IsNullOrEmpty(display) ? _diff.Name(tree, item.Node) : display);
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
        _marks.Toggle(Selection().Where(i => !DiffActions.IsCategory(i)));
        _grid.Invalidate();
        UpdateStatus();
    }

    private async Task AddToIgnoreListAsync()
    {
        if (_diff is null)
        {
            return;
        }

        var selection = Selection().Where(i => !DiffActions.IsCategory(i)).ToList();
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

        var nodes = DiffActions.Nodes(_diff, Selection(), DiffArea.Registry);
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

        var nodes = DiffActions.Nodes(_diff, Selection(), DiffArea.FileSystem);
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

        var nodes = DiffActions.Nodes(_diff, Selection(), DiffArea.Registry);
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
