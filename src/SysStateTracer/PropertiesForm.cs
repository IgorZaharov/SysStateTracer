using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;
using SysStateTracer.Core.Storage;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>W2 Snapshot Properties (plan §10.2, Step 46): everything stored about a snapshot, and its skipped items.</summary>
internal sealed class PropertiesForm : ThemedDialog
{
    /// <summary>Width the values wrap at: the window less the captions, the cards' padding, margins and a scroll bar.</summary>
    private const int ValueWidth = 360;

    /// <summary>The groups, each a heading and a card, one below the other (Windows 11 settings).</summary>
    private readonly TableLayoutPanel _sections = new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Padding = new Padding(0, 0, 4, 0),
    };

    private readonly List<(LayerPanel Card, TableLayoutPanel Grid)> _cards = [];
    private readonly Panel _scroller;
    private TableLayoutPanel? _grid;

    public PropertiesForm(string path)
        : base("Snapshot Properties")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Window size, not client size: no window is taller than 580 px at 100 % scaling (F14).
        Size = new Size(640, 580);
        _sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _scroller = scroller;
        scroller.Controls.Add(_sections);
        ThemeManager.SetRole(this, ThemeRole.Surface);
        ThemeManager.SetRole(scroller, ThemeRole.Surface);
        ThemeManager.SetRole(_sections, ThemeRole.Surface);
        Controls.Add(scroller);
        scroller.BringToFront();
        AddButton("Close", DialogResult.Cancel, primary: true);

        try
        {
            using var reader = SnapshotReader.Open(path);
            var skipped = reader.Load(SnapshotParts.Skipped).Skipped;
            Fill(reader.Info, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
        {
            Group("Error");
            Row("Error", Core.Config.FriendlyErrors.Describe(ex));
        }

        FitCards();
    }

    private void Fill(SnapshotInfo info, IReadOnlyList<SkippedItem> skipped)
    {
        var meta = info.Meta;
        var counts = meta.Counts;
        var t = meta.Timings;

        Group("General");
        Row("Name", info.Name);
        Row("File", info.Path);
        Row("Size", $"{ItemFormat.Size(info.FileLength)} ({info.FileLength.ToString("N0", CultureInfo.CurrentCulture)} bytes)");
        Row("Created", $"{meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} local · {meta.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        Row("Computer", meta.ComputerName);
        Row("User", meta.UserName + meta.TakenAsAdministrator switch
        {
            true => " (as administrator)",
            false => " (standard user: folders and keys that need administrator rights were skipped)",
            _ => string.Empty,
        });
        Row("Windows", $"{meta.OsEdition} ({meta.OsVersion})");
        Row("Taken with", $"SysStateTracer {meta.AppVersion} · file format {info.FormatMajor}.{info.FormatMinor}");

        Group("Timing");
        Row("Total", Seconds(t.TotalMs));
        Row("File system", Seconds(t.FileSystemMs) + (meta.FileSystemMethods.Count > 0 ? $" · {string.Join(", ", meta.FileSystemMethods)}" : string.Empty));
        Row("Registry", Seconds(t.RegistryMs));
        Row("Services", Seconds(t.ServicesMs));
        Row("Writing", Seconds(t.WriteMs));
        Row("Mode", meta.Mode == SnapshotMode.Full
            ? "Full" + (string.IsNullOrEmpty(meta.ModeReason) ? string.Empty : $" ({meta.ModeReason})")
            : "Incremental: unchanged parts were reused from the previous snapshot" + (string.IsNullOrEmpty(meta.ModeReason) ? string.Empty : $" ({meta.ModeReason})"));

        Group("Scope");
        Row("Included", meta.Scope.Count == 0 ? "(nothing)" : string.Join(Environment.NewLine, meta.Scope));
        Row("Global Ignore rules", meta.IgnoreRuleCount.ToString("N0", CultureInfo.CurrentCulture));

        Group("Counts");
        Row("Folders", N(counts.Folders));
        Row("Files", N(counts.Files));
        Row("Registry keys", N(counts.RegistryKeys));
        Row("Registry values", N(counts.RegistryValues));
        Row("Services", N(counts.Services));
        Row("Skipped (not readable)", N(counts.Skipped));

        if (skipped.Count > 0)
        {
            var expander = new ThemedButton { Text = $"Show skipped items ({skipped.Count:N0})", Width = 220, Margin = new Padding(0, 12, 0, 0) };
            var list = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Visible = false,
                Height = 200,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 8, 0, 0),
            };
            expander.Click += (_, _) =>
            {
                if (list.Text.Length == 0)
                {
                    list.Text = string.Join(Environment.NewLine, skipped.Select(s => $"{s.Path}    {Win32Errors.Describe(s.Error)}"));
                }

                list.Visible = !list.Visible;
                expander.Text = (list.Visible ? "Hide" : "Show") + $" skipped items ({skipped.Count:N0})";
            };
            _sections.Controls.Add(expander);
            _sections.Controls.Add(list);
        }

        var note = new Label
        {
            Text = "Snapshots contain registry data, which can include sensitive settings such as product keys. Treat the file like a registry backup.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 16, 0, 0),
        };
        ThemeManager.SetRole(note, ThemeRole.Secondary);
        _sections.Controls.Add(note);
    }

    private static string Seconds(long ms) => ms <= 0 ? "–" : string.Create(CultureInfo.CurrentCulture, $"{ms / 1000.0:0.0} s");

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Starts a group: its heading, and a card that the following rows go on.</summary>
    private void Group(string title)
    {
        var label = new Label { Text = title, AutoSize = true, Font = UiFonts.Bold, Margin = new Padding(0, _sections.Controls.Count == 0 ? 0 : 16, 0, 6) };
        _sections.Controls.Add(label);
        _grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var card = new LayerPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0) };
        card.Controls.Add(_grid);
        _sections.Controls.Add(card);
        _cards.Add((card, _grid));
    }

    /// <summary>Each card as tall as its rows, whose heights do not depend on the width (values wrap at <see cref="ValueWidth"/>).</summary>
    private void FitCards()
    {
        foreach (var (card, grid) in _cards)
        {
            card.Height = grid.PreferredSize.Height + card.Padding.Vertical;
        }
    }

    private void Row(string name, string value)
    {
        var caption = new Label { Text = name, AutoSize = true, Margin = new Padding(0, 3, 8, 3) };
        ThemeManager.SetRole(caption, ThemeRole.Secondary);
        // Long values (how each folder was read, why a snapshot was incremental) wrap instead of being cut off.
        var text = new TextBox
        {
            Text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.None,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 0, 3),
            TabStop = false,
        };
        var wrapped = TextRenderer.MeasureText(text.Text, UiFonts.Normal, new Size(ValueWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        text.Height = Math.Max(UiFonts.Normal.Height, wrapped.Height) + 4;

        ThemeManager.SetRole(text, ThemeRole.Default);
        _grid!.Controls.Add(caption);
        _grid.Controls.Add(text);
        Plain.Add(text);
    }

    /// <summary>Value boxes look like labels: selectable text on the window background.</summary>
    private List<TextBox> Plain { get; } = [];

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RecolorValues();
        ThemeManager.Changed += OnThemeChanged;
        FormClosed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ThemeManager.Post(this, RecolorValues);

    private void RecolorValues()
    {
        foreach (var box in Plain)
        {
            box.BackColor = ThemeManager.Current.Background;
        }

        NativeUi.SetExplorerTheme(_scroller.Handle, ThemeManager.Current.IsDark); // a dark scroll bar in the dark theme
    }
}
