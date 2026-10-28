using System.Diagnostics;
using System.Globalization;
using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Export;
using SysStateTracer.Theming;

namespace SysStateTracer;

/// <summary>
/// Copy to .\Files (plan 2.6): progress bar, files and bytes copied, the current file, elapsed time and Cancel;
/// at the end, the files that were skipped and why.
/// </summary>
internal sealed class CopyProgressForm : ThemedDialog
{
    private readonly IReadOnlyList<FileToCopy> _files;
    private readonly string _target;
    private readonly ThemedProgressBar _progress = new() { Dock = DockStyle.Top, Height = 6 };
    private readonly Label _counts = new() { AutoSize = true, Text = " " };
    private readonly Label _current = new() { AutoSize = false, AutoEllipsis = true, Height = 20, Dock = DockStyle.Top };
    private readonly TextBox _skipped = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Visible = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly ThemedButton _cancel;
    private readonly ThemedButton _open;
    private readonly CancellationTokenSource _stop = new();
    private bool _running;

    public CopyProgressForm(IReadOnlyList<FileToCopy> files, string targetFolder)
        : base("Copy to Files")
    {
        _files = files;
        _target = targetFolder;
        ClientSize = new Size(640, 220);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(520, 220);

        var layout = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = string.Create(CultureInfo.CurrentCulture, $"Copying {files.Count:N0} new and modified files ({ItemFormat.Size(files.Sum(f => f.Size))}) to {targetFolder}"),
            AutoSize = false,
            AutoEllipsis = true,
            Height = 24,
            Dock = DockStyle.Top,
        };
        var progressHolder = new Panel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(0, 8, 0, 8) };
        progressHolder.Controls.Add(_progress);
        var countsHolder = new Panel { Dock = DockStyle.Top, Height = 22 };
        countsHolder.Controls.Add(_counts);
        ThemeManager.SetRole(_current, ThemeRole.Secondary);
        layout.Controls.Add(_skipped);
        layout.Controls.Add(_current);
        layout.Controls.Add(countsHolder);
        layout.Controls.Add(progressHolder);
        layout.Controls.Add(title);
        Controls.Add(layout);
        layout.BringToFront();

        _cancel = AddButton("Cancel", DialogResult.None, primary: true);
        _open = AddButton("Open folder", DialogResult.None);
        _open.Visible = false;
        _cancel.Click += (_, _) =>
        {
            if (_running)
            {
                _cancel.Enabled = false;
                _cancel.Text = "Stopping…";
                _stop.Cancel();
            }
            else
            {
                Close();
            }
        };
        _open.Click += (_, _) =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_target}\"") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                MessageDialog.Error(this, "Explorer could not be opened.", ex);
            }
        };
    }

    /// <summary>The result, once finished.</summary>
    public CopyReport? Report { get; private set; }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _running = true;
        var progress = new Progress<CopyProgress>(p =>
        {
            _progress.Value = p.Fraction;
            _counts.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{p.FilesDone:N0} of {p.FilesTotal:N0} files · {ItemFormat.Size(p.BytesDone)} of {ItemFormat.Size(p.BytesTotal)} · {p.Elapsed:hh\\:mm\\:ss}");
            _current.Text = p.CurrentFile ?? string.Empty;
        });
        try
        {
            Report = await Task.Run(() => FileCopier.Copy(_files, _target, progress, _stop.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _running = false;
            MessageDialog.Error(this, "Copying stopped.", ex);
            Close();
            return;
        }

        _running = false;
        var report = Report;
        _progress.Value = report.Cancelled ? _progress.Value : 1;
        _counts.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{(report.Cancelled ? "Cancelled" : "Done")}: {report.Copied:N0} copied ({ItemFormat.Size(report.BytesCopied)}), {report.Skipped.Count:N0} skipped.");
        _current.Text = report.Skipped.Count > 0 ? "Skipped files:" : string.Empty;
        if (report.Skipped.Count > 0)
        {
            _skipped.Text = string.Join(Environment.NewLine, report.Skipped.Select(s => $"{s.Source}    {s.Reason}"));
            _skipped.Visible = true;
            Height += 200;
        }

        if (report.Copied > 0)
        {
            try
            {
                _ = RestoreScript.Write(_target);
                _current.Text = (report.Skipped.Count > 0 ? "Skipped files below. " : string.Empty) +
                    $"{RestoreScript.FileName} in the folder copies the files back to their places.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("The restore script could not be written", ex);
            }
        }

        _cancel.Text = "Close";
        _cancel.Enabled = true;
        _open.Visible = report.Copied > 0;
        Log.Info($"Copied {report.Copied} files to {_target}; {report.Skipped.Count} skipped; cancelled: {report.Cancelled}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            _stop.Cancel();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stop.Dispose();
        }

        base.Dispose(disposing);
    }
}
