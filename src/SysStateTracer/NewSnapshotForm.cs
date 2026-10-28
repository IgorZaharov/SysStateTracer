using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Scanning;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Core.Snapshots;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// W3 New Snapshot (plan §10.3, Step 49): name, scope tree, Full rescan, Start / Stop with progress bar,
/// a timer ticking every second, the current phase and path, and items per second.
/// </summary>
internal sealed class NewSnapshotForm : ThemedDialog
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly TextBox _name = new() { BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
    private readonly Label _nameProblem = new() { AutoSize = true, Text = " " };
    private readonly LinkLabel _elevateLink = new() { Text = "Restart SysStateTracer as administrator", AutoSize = true, Visible = false };
    private readonly Label _notElevated = new()
    {
        AutoSize = true,
        Visible = false,
        MaximumSize = new Size(700, 0),
        Text = "Not running as administrator: folders and registry keys that need administrator rights are greyed out and skipped, and whole drives are read more slowly.",
    };
    private readonly Label _folder = new() { AutoSize = true, AutoEllipsis = true };
    private readonly ScopeTree _tree = new() { Dock = DockStyle.Fill };
    private readonly Label _ignoreStatus = new() { AutoSize = true, Text = "Reading the Global Ignore list…" };
    private readonly CheckBox _fullRescan = new() { Text = "Full rescan: read everything again instead of reusing unchanged parts of the previous snapshot", AutoSize = true };
    private readonly ThemedProgressBar _progress = new() { Dock = DockStyle.Top, Height = 6 };
    private readonly Label _phase = new() { AutoSize = true, Text = "Ready." };
    private readonly Label _elapsed = new() { AutoSize = true, Text = "00:00:00", Anchor = AnchorStyles.Right };
    private readonly Label _current = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20 };
    private readonly Label _rate = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly ThemedButton _start;
    private readonly ThemedButton _stop;
    private readonly ThemedButton _close;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private readonly Stopwatch _watch = new();
    private Task<IgnoreListState>? _ignoreLoad;
    private CancellationTokenSource? _cancel;
    private bool _running;
    private bool _closeWhenStopped;

    public NewSnapshotForm(AppPaths paths, AppSettings settings, bool? elevated = null)
        : base("New Snapshot")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        _paths = paths;
        _settings = settings;
        ClientSize = new Size(760, 660);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(620, 560);

        BuildLayout();
        var standardUser = !(elevated ?? Core.Interop.ProcessPrivileges.IsElevated);
        _notElevated.Visible = standardUser;
        _elevateLink.Visible = standardUser;
        _tree.Elevated = !standardUser;
        _elevateLink.LinkClicked += (_, _) =>
        {
            if (!IsRunning && Elevation.RestartAsAdministrator(this))
            {
                Close();
            }
        };
        _close = AddButton("Close", DialogResult.Cancel);
        _stop = AddButton("Stop", DialogResult.None);
        _start = AddButton("Start", DialogResult.None, primary: true);
        _start.Glyph = Glyphs.Play;
        _stop.Glyph = Glyphs.Stop;
        _stop.Enabled = false;
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => Stop();
        AcceptButton = null;

        _name.Text = SnapshotNaming.DefaultName();
        _name.TextChanged += (_, _) => ValidateName();
        _folder.Text = "Saved in " + paths.SnapshotDirectory(settings);
        _tree.IgnoredItemClicked += (_, item) => MessageDialog.Inform(
            this,
            "Ignored item",
            $"{item.Path} is in the Global Ignore list, so it is always left out. Remove its rule in Preferences → Global Ignore list to include it.");
        _tree.NoAccessItemClicked += (_, item) => MessageDialog.Inform(
            this,
            "No access",
            $"Your user account cannot read {item.Path}, so it is left out. Restart SysStateTracer as administrator to include it.");
        _clock.Tick += (_, _) => _elapsed.Text = FormatElapsed(_watch.Elapsed);

        IReadOnlyList<VolumeInfo> volumes;
        try
        {
            volumes = VolumeDiscovery.GetFixedVolumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warning("Fixed drives could not be listed", ex);
            volumes = [];
        }

        _tree.Load(settings.Scope, volumes);
    }

    /// <summary>Path of the snapshot taken in this window, for W1 to select.</summary>
    public string? CreatedPath { get; private set; }

    internal ScopeTree Tree => _tree;

    internal TextBox NameBox => _name;

    internal bool IsRunning => _running;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _notElevated.ForeColor = ThemeManager.Current.Modified;
        ValidateName();
        _ignoreLoad = LoadIgnoreListAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsRunning)
        {
            e.Cancel = true;
            if (!_closeWhenStopped && MessageDialog.Ask(this, "New Snapshot", "A snapshot is being taken. Stop it and close the window? Nothing is saved.", "Stop and close", "Keep going"))
            {
                _closeWhenStopped = true;
                Stop();
            }

            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            if (_running)
            {
                _cancel?.Cancel(); // the snapshot task still holds the token; it ends and nothing is saved
            }
            else
            {
                _cancel?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // name
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // folder / problem
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // scope heading
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // tree
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // ignore status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // options
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // progress
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // details

        var nameRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "Name", AutoSize = true, Margin = new Padding(0, 5, 12, 0) }, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);
        layout.Controls.Add(nameRow);

        var info = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 4, 0, 0), WrapContents = false };
        ThemeManager.SetRole(_folder, ThemeRole.Secondary);
        info.Controls.Add(_folder);
        info.Controls.Add(_nameProblem);
        info.Controls.Add(_notElevated);
        info.Controls.Add(_elevateLink);
        layout.Controls.Add(info);

        var scopeTitle = new Label { Text = "What to include", AutoSize = true, Margin = new Padding(0, 8, 0, 6) };
        ThemeManager.SetRole(scopeTitle, ThemeRole.Heading);
        layout.Controls.Add(scopeTitle);

        var treeBorder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0) };
        treeBorder.Controls.Add(_tree);
        treeBorder.Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeManager.Current.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, treeBorder.Width - 1, treeBorder.Height - 1);
        };
        layout.Controls.Add(treeBorder);

        ThemeManager.SetRole(_ignoreStatus, ThemeRole.Secondary);
        _ignoreStatus.Margin = new Padding(0, 6, 0, 0);
        layout.Controls.Add(_ignoreStatus);
        _fullRescan.Margin = new Padding(0, 8, 0, 0);
        layout.Controls.Add(_fullRescan);

        var progressBox = new Panel { Dock = DockStyle.Top, Height = 22, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(0, 8, 0, 8) };
        progressBox.Controls.Add(_progress);
        layout.Controls.Add(progressBox);

        var details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.Controls.Add(_phase, 0, 0);
        details.Controls.Add(_elapsed, 1, 0);
        details.Controls.Add(_current, 0, 1);
        details.Controls.Add(_rate, 1, 1);
        _phase.Font = UiFonts.Bold;
        _elapsed.Font = UiFonts.Bold;
        ThemeManager.SetRole(_current, ThemeRole.Secondary);
        ThemeManager.SetRole(_rate, ThemeRole.Secondary);
        layout.Controls.Add(details);

        Controls.Add(layout);
        layout.BringToFront();
    }

    private async Task<IgnoreListState> LoadIgnoreListAsync()
    {
        IgnoreListState state;
        try
        {
            using var http = new HttpClient();
            state = await IgnoreListLoader.LoadAsync(
                _settings.IgnoreListSource,
                _settings.IgnoreListRemoteLocation,
                new LocalIgnoreFile(_paths.LocalIgnoreFile),
                http);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Log.Warning("Global Ignore list could not be read", ex);
            state = new IgnoreListState(GlobalIgnoreList.FromText(DefaultIgnoreRules.Text, IgnoreRuleSource.Local), IgnoreListOrigin.Local, "The built-in rules are used.");
        }

        if (!IsDisposed)
        {
            _tree.SetIgnore(state.List.Matcher);
            var origin = state.Origin switch
            {
                IgnoreListOrigin.Remote => "remote list",
                IgnoreListOrigin.LocalFallback => "local list (the remote list could not be read)",
                _ => "local list",
            };
            _ignoreStatus.Text = $"Global Ignore list: {origin}, {state.List.Rules.Count:N0} rules. Ignored items are always left out.";
        }

        return state;
    }

    private string? NameProblem()
    {
        var name = _name.Text.Trim();
        if (!SnapshotNaming.IsValidName(name))
        {
            return "Not a valid file name: no \\ / : * ? \" < > | and no trailing dot or space.";
        }

        return File.Exists(Path.Combine(_paths.SnapshotDirectory(_settings), name + SnapshotNaming.FileExtension))
            ? "A snapshot with this name already exists."
            : null;
    }

    private void ValidateName()
    {
        var problem = NameProblem();
        _nameProblem.Text = problem ?? " ";
        _nameProblem.ForeColor = ThemeManager.Current.Deleted;
        _start.Enabled = problem is null && !IsRunning && CreatedPath is null;
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running && CreatedPath is null && NameProblem() is null;
        _stop.Enabled = running;
        _name.Enabled = !running && CreatedPath is null;
        _tree.Enabled = !running && CreatedPath is null;
        _fullRescan.Enabled = !running && CreatedPath is null;
    }

    private async Task StartAsync()
    {
        if (IsRunning || NameProblem() is not null)
        {
            return;
        }

        if (_tree.IsEmpty)
        {
            MessageDialog.Inform(this, "New Snapshot", "Nothing is selected. Check at least one drive, registry hive or the services.");
            return;
        }

        var name = _name.Text.Trim();
        var scope = _tree.ToScope();
        _settings.Scope = scope;
        try
        {
            SettingsStore.Save(_paths.ConfigFile, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("The scope could not be saved", ex);
        }

        _running = true;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        SetRunning(true);
        _phase.Text = "Starting…";
        _current.Text = string.Empty;
        _rate.Text = string.Empty;
        _progress.Marquee = true;
        _watch.Restart();
        _elapsed.Text = FormatElapsed(TimeSpan.Zero);
        _clock.Start();

        var ignore = _ignoreLoad is null ? null : await _ignoreLoad;
        var folder = _paths.SnapshotDirectory(_settings);
        var request = SnapshotRequest.ForScope(name, folder, scope, ignore?.List ?? GlobalIgnoreList.Empty, _paths.CacheFile, _fullRescan.Checked);
        var progress = new Progress<ScanProgress>(ShowProgress);
        string summary;
        var failed = false;
        try
        {
            var result = await Task.Run(() => SnapshotOrchestrator.Run(request, progress, token), token);
            CreatedPath = result.Path;
            var meta = result.Meta;
            var c = meta.Counts;
            var items = c.Folders + c.Files + c.RegistryKeys + c.RegistryValues + c.Services;
            var skippedNote = c.Skipped > 0
                ? string.Create(CultureInfo.CurrentCulture, $", {c.Skipped:N0} not readable (see Properties).")
                : ".";
            var mode = meta.Mode == SnapshotMode.Full ? "full scan" : "incremental";
            summary = string.Create(CultureInfo.CurrentCulture, $"Done: {items:N0} items in {FormatElapsed(_watch.Elapsed)}, {mode}{skippedNote}");
            Log.Info($"Snapshot taken: {result.Path}");
        }
        catch (OperationCanceledException)
        {
            summary = "Stopped. No snapshot was saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            summary = "The snapshot could not be taken: " + FriendlyErrors.Describe(ex);
            failed = true;
            Log.Error("Snapshot failed", ex);
        }
        finally
        {
            _running = false;
            _clock.Stop();
            _watch.Stop();
            _progress.Marquee = false;
        }

        if (IsDisposed || Disposing)
        {
            return; // the window went away while the snapshot was stopping
        }

        _elapsed.Text = FormatElapsed(_watch.Elapsed);
        _phase.Text = summary;
        _phase.ForeColor = failed ? ThemeManager.Current.Deleted : CreatedPath is null ? ThemeManager.Current.Text : ThemeManager.Current.New;
        _current.Text = CreatedPath ?? string.Empty;
        _progress.Value = CreatedPath is null ? 0 : 1;
        SetRunning(false);
        if (CreatedPath is not null)
        {
            _close.Primary = true;
            _start.Primary = false;
            AcceptButton = _close;
            _close.Focus();
        }

        if (_closeWhenStopped)
        {
            Close();
        }
    }

    private void Stop()
    {
        if (IsRunning)
        {
            _stop.Enabled = false;
            _phase.Text = "Stopping…";
            _cancel?.Cancel();
        }
    }

    private void ShowProgress(ScanProgress p)
    {
        if (!IsRunning || _cancel?.IsCancellationRequested == true)
        {
            return;
        }

        _phase.Text = p.Phase switch
        {
            SnapshotPhase.Starting => "Starting…",
            SnapshotPhase.Writing => "Writing the snapshot file…",
            SnapshotPhase.Done => "Finishing…",
            _ => string.Create(CultureInfo.CurrentCulture, $"Reading: {p.FileSystemEntries:N0} files and folders · {p.RegistryKeys:N0} registry keys · {p.Services:N0} services"),
        };
        _current.Text = p.CurrentPath ?? string.Empty;
        var seconds = p.Elapsed.TotalSeconds;
        var total = p.FileSystemEntries + p.RegistryKeys + p.Services;
        _rate.Text = seconds > 0.5 ? string.Create(CultureInfo.CurrentCulture, $"{total / seconds:N0} items/s") : string.Empty;
        if (p.Fraction is { } fraction)
        {
            _progress.Marquee = false;
            _progress.Value = fraction;
        }
        else
        {
            _progress.Marquee = true;
        }

    }
}
