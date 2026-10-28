using SysStateTracer.Core;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>Base for the small dialogs: fixed frame, 16 px padding, buttons bottom-right, themed.</summary>
internal class ThemedDialog : Form
{
    public ThemedDialog(string title)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Value;
        ShowIcon = false;
        Padding = new Padding(16);

        Buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 16, 0, 0),
            WrapContents = false,
        };
        Controls.Add(Buttons);
    }

    /// <summary>Right-aligned button row; the first added is rightmost.</summary>
    protected FlowLayoutPanel Buttons { get; }

    protected ThemedButton AddButton(string text, DialogResult result, bool primary = false)
    {
        var button = new ThemedButton { Text = text, DialogResult = result, Primary = primary, Margin = new Padding(8, 0, 0, 0), Width = 96 };
        button.Width = Math.Max(96, TextRenderer.MeasureText(text, UiFonts.Normal).Width + 32);
        Buttons.Controls.Add(button);
        if (primary)
        {
            AcceptButton = button;
        }

        if (result is DialogResult.Cancel or DialogResult.No)
        {
            CancelButton = button;
        }

        return button;
    }

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Attach(this);
        base.OnLoad(e);
    }
}

/// <summary>Question, warning and information boxes in the app's look (MessageBox cannot be themed).</summary>
internal sealed class MessageDialog : ThemedDialog
{
    private MessageDialog(string title, string message, IReadOnlyList<string>? details)
        : base(title)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var label = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0),
        };
        layout.Controls.Add(label);
        if (details is { Count: > 0 })
        {
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = string.Join(Environment.NewLine, details),
                Size = new Size(520, Math.Min(220, 22 + (details.Count * 16))),
                Margin = new Padding(0, 12, 0, 0),
                BorderStyle = BorderStyle.FixedSingle,
            };
            layout.Controls.Add(box);
        }

        Controls.Add(layout);
        layout.BringToFront();
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(360, 0);
    }

    /// <summary>Yes/No question; true for yes.</summary>
    public static bool Ask(IWin32Window? owner, string title, string message, string yes = "Yes", string no = "No", IReadOnlyList<string>? details = null)
    {
        using var dialog = new MessageDialog(title, message, details);
        dialog.AddButton(no, DialogResult.No);
        dialog.AddButton(yes, DialogResult.Yes, primary: true);
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }

    public static void Inform(IWin32Window? owner, string title, string message, IReadOnlyList<string>? details = null)
    {
        using var dialog = new MessageDialog(title, message, details);
        dialog.AddButton("OK", DialogResult.OK, primary: true);
        dialog.CancelButton = dialog.AcceptButton;
        dialog.ShowDialog(owner);
    }

    /// <summary>Shows an error, with a pointer to the log for the details.</summary>
    public static void Error(IWin32Window? owner, string message, Exception? exception = null)
    {
        Core.Config.Log.Error(message, exception);
        var text = exception is null ? message : $"{message}{Environment.NewLine}{Environment.NewLine}{exception.Message}";
        Inform(owner, AppInfo.Name, text);
    }
}

/// <summary>A one-line text input with live validation (Rename Snapshot, Add registry key).</summary>
internal sealed class InputDialog : ThemedDialog
{
    private readonly TextBox _box;
    private readonly Label _problem;
    private readonly ThemedButton _ok;
    private readonly Func<string, string?> _validate;

    private InputDialog(string title, string prompt, string initial, string okText, Func<string, string?> validate)
        : base(title)
    {
        _validate = validate;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        _box = new TextBox { Text = initial, Width = 420, Margin = new Padding(0), BorderStyle = BorderStyle.FixedSingle };
        layout.Controls.Add(_box);
        _problem = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 0, 0), Text = " " };
        layout.Controls.Add(_problem);
        Controls.Add(layout);
        layout.BringToFront();

        AddButton("Cancel", DialogResult.Cancel);
        _ok = AddButton(okText, DialogResult.OK, primary: true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _box.TextChanged += (_, _) => Check();
        Shown += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
            Check();
        };
    }

    public string Value => _box.Text.Trim();

    /// <summary>The entered text, or null when cancelled. <paramref name="validate"/> returns a problem or null.</summary>
    public static string? Ask(IWin32Window? owner, string title, string prompt, string initial, string okText, Func<string, string?> validate)
    {
        using var dialog = new InputDialog(title, prompt, initial, okText, validate);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Value : null;
    }

    private void Check()
    {
        var problem = _validate(Value);
        _problem.Text = problem ?? " ";
        _problem.ForeColor = ThemeManager.Current.Deleted;
        _ok.Enabled = problem is null;
    }
}
