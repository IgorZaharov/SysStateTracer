using SysStateTracer.Core;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Interop;

namespace SysStateTracer;

/// <summary>
/// Placeholder main window for the skeleton build (plan Steps 4–8).
/// The real W1 layout — sidebar, snapshot list, sorting — replaces it at Step 43.
/// </summary>
internal sealed class MainForm : Form
{
    public MainForm(AppPaths paths, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);

        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Text = AppInfo.Name;
        Icon = AppIcon.Value;

        var nl = Environment.NewLine;
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"{AppInfo.Name} {AppInfo.Version}{nl}{nl}" +
                   $"Data folder: {paths.DataDirectory}{(paths.UsesFallback ? " (exe folder is not writable)" : string.Empty)}{nl}" +
                   $"Snapshot folder: {paths.SnapshotDirectory(settings)}{nl}" +
                   $"Running as administrator: {(ProcessPrivileges.IsElevated ? "yes" : "no")}{nl}{nl}" +
                   "Skeleton build: the main window arrives at plan Step 43.",
        });

        ResumeLayout(performLayout: true);
    }
}
