using System.Diagnostics;
using SysStateTracer.Controls;
using SysStateTracer.Core;
using SysStateTracer.Core.CommandLine;
using SysStateTracer.Core.Config;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>W6 Help/About (plan §11.5, Step 48): icon, name, version, GitHub link and the command-line help.</summary>
internal sealed class AboutForm : ThemedDialog
{
    public AboutForm(AppPaths paths, AppSettings settings)
        : base("About " + AppInfo.Name)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        FormBorderStyle = FormBorderStyle.Sizable;
        // Window size, not client size: no window is taller than 580 px at 100 % scaling (F14).
        Size = new Size(760, 560);
        MinimumSize = new Size(560, 420);
        WindowLayout.Remember(this, "About", paths, settings, keepPosition: false);

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 16, 0) };
        if (AppIcon.Value is { } appIcon)
        {
            icon.Image = new Icon(appIcon, 64, 64).ToBitmap();
        }

        header.Controls.Add(icon, 0, 0);
        header.SetRowSpan(icon, 3);
        var name = new Label { Text = AppInfo.Name, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
        ThemeManager.SetRole(name, ThemeRole.Heading);
        header.Controls.Add(name, 1, 0);
        var version = new Label
        {
            Text = $"Version {AppInfo.Version} · snapshots of folders, files, registry and services, and what changed between them",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 2),
        };
        ThemeManager.SetRole(version, ThemeRole.Secondary);
        header.Controls.Add(version, 1, 1);
        var link = new LinkLabel { Text = AppInfo.GitHubUrl, AutoSize = true, Margin = new Padding(0) };
        link.LinkClicked += (_, _) => OpenLink(AppInfo.GitHubUrl);
        header.Controls.Add(link, 1, 2);

        var caption = new Label { Text = "Command line", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 6) };
        ThemeManager.SetRole(caption, ThemeRole.Heading);
        var help = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.FixedSingle,
            Text = CliHelp.Text(paths.SnapshotDirectory(settings), paths.DataDirectory),
        };
        Controls.Add(help);
        Controls.Add(caption);
        Controls.Add(header);
        help.BringToFront();
        AddButton("Close", DialogResult.Cancel, primary: true);
    }

    private void OpenLink(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageDialog.Error(this, "The browser could not be opened.", ex);
        }
    }
}
