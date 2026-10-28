using SysStateTracer.Controls;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// Global Ignore rules to check and change before they are saved, one per line (F20, F25): used by
/// "Add to Global Ignore list" in the comparison window and by Add rule / Edit in Preferences. Each line is
/// checked as it is typed; the main button is enabled only when every line is a valid rule.
/// </summary>
internal sealed class IgnoreRulesDialog : ThemedDialog
{
    private static readonly Font Mono = new("Consolas", 9.5F);

    private readonly TextBox _box;
    private readonly Label _problem;
    private readonly ThemedButton _ok;

    private IgnoreRulesDialog(string title, string prompt, IEnumerable<string> rules, string okText)
        : base(title)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 8) });
        _box = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = Mono,
            Size = new Size(560, 140),
            Margin = new Padding(0),
            BorderStyle = BorderStyle.FixedSingle,
            Text = string.Join(Environment.NewLine, rules),
        };
        layout.Controls.Add(_box);
        var hint = new Label
        {
            Text = "One rule per line, starting with FS: (folder or file), REG: (key or value) or SVC: (service). "
                + "* matches any text, also across \\ (FS:C:\\Users\\*\\AppData\\Local\\Temp, FS:C:\\Temp\\*); "
                + "? matches one character. A rule also covers everything below the folder or key.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 8, 0, 0),
        };
        ThemeManager.SetRole(hint, ThemeRole.Secondary);
        layout.Controls.Add(hint);
        _problem = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 6, 0, 0), Text = " " };
        layout.Controls.Add(_problem);
        Controls.Add(layout);
        layout.BringToFront();

        AddButton("Cancel", DialogResult.Cancel);
        _ok = AddButton(okText, DialogResult.OK, primary: true);
        AcceptButton = null; // Enter starts a new line in the box; Ctrl+Enter or the button saves
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _box.TextChanged += (_, _) => Check();
        Shown += (_, _) =>
        {
            _box.Focus();
            _box.SelectionStart = _box.TextLength;
            Check();
        };
    }

    /// <summary>The rule lines, trimmed, without blank lines.</summary>
    public IReadOnlyList<string> Rules => Lines(_box.Text);

    /// <summary>The edited rules, or null when cancelled.</summary>
    public static IReadOnlyList<string>? Edit(IWin32Window? owner, string title, string prompt, IEnumerable<string> rules, string okText)
    {
        using var dialog = Create(title, prompt, rules, okText);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Rules : null;
    }

    /// <summary>The dialog, not shown yet (tests and screenshots).</summary>
    internal static IgnoreRulesDialog Create(string title, string prompt, IEnumerable<string> rules, string okText) =>
        new(title, prompt, rules, okText);

    internal TextBox Box => _box;

    internal bool CanSave => _ok.Enabled;

    /// <summary>The problem with the first line that is not a valid rule, or null.</summary>
    internal static string? Problem(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return "Enter at least one rule.";
        }

        foreach (var line in lines)
        {
            if (!IgnoreRuleParser.TryParse(line, IgnoreRuleSource.Local, out _, out var error))
            {
                return $"\"{line}\": {error}";
            }
        }

        return null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter) && _ok.Enabled)
        {
            _ok.PerformClick();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static List<string> Lines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')];

    private void Check()
    {
        var problem = Problem(Rules);
        _problem.Text = problem ?? " ";
        _problem.ForeColor = ThemeManager.Current.Deleted;
        _ok.Enabled = problem is null;
    }
}
