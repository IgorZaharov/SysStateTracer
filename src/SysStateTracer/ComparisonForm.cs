using SysStateTracer.Core.Config;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>W4 Snapshot Comparison (plan §10.4). Temporary shell until Step 51 lands.</summary>
internal sealed class ComparisonForm : Form
{
    public ComparisonForm(AppPaths paths, AppSettings settings, string olderPath, string newerPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFonts.Normal;
        Icon = AppIcon.Value;
        ClientSize = new Size(900, 600);
        Text = $"{Path.GetFileNameWithoutExtension(olderPath)} ⇄ {Path.GetFileNameWithoutExtension(newerPath)}";
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "The comparison window is being built (plan Step 51)." });
        ThemeManager.Attach(this);
    }
}
