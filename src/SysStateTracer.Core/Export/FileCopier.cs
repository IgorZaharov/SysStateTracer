namespace SysStateTracer.Core.Export;

/// <summary>Progress of a copy to .\Files, for the progress window (plan 2.6).</summary>
public sealed record CopyProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? CurrentFile, TimeSpan Elapsed)
{
    public double Fraction => BytesTotal > 0 ? Math.Min(1, BytesDone / (double)BytesTotal) : FilesTotal > 0 ? FilesDone / (double)FilesTotal : 1;
}

/// <summary>A file that could not be copied, and why (locked, deleted since the snapshot, no access).</summary>
public sealed record CopySkip(string Source, string Reason);

public sealed record CopyReport(int Copied, long BytesCopied, IReadOnlyList<CopySkip> Skipped, bool Cancelled);

/// <summary>
/// Copies new and modified files from the live disk to .\Files, keeping the folder structure (plan 2.6, 2.7).
/// Files are read with all sharing modes so files open in other programs can usually still be copied;
/// files that cannot be read are reported, not fatal. Cancel stops after the current block and keeps what
/// was copied; the partly copied file is removed.
/// </summary>
public static class FileCopier
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    public static CopyReport Copy(
        IReadOnlyList<FileToCopy> files, string targetRoot, IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        var total = files.Sum(f => f.Size);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.MinValue;
        var skipped = new List<CopySkip>();
        var buffer = new byte[BufferSize];
        long bytesDone = 0;
        var copied = 0;
        long bytesCopied = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (cancellationToken.IsCancellationRequested)
            {
                return new CopyReport(copied, bytesCopied, skipped, Cancelled: true);
            }

            Report(i, file.Source, force: true);
            var target = Path.Combine(targetRoot, file.RelativeTarget);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var written = CopyOne(file.Source, target, buffer, done =>
                {
                    Report(i, file.Source, force: false, extra: done);
                }, cancellationToken);
                copied++;
                bytesCopied += written;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target);
                return new CopyReport(copied, bytesCopied, skipped, Cancelled: true);
            }
            catch (FileNotFoundException)
            {
                skipped.Add(new CopySkip(file.Source, "deleted since the snapshot"));
            }
            catch (DirectoryNotFoundException)
            {
                skipped.Add(new CopySkip(file.Source, "deleted since the snapshot"));
            }
            catch (UnauthorizedAccessException ex)
            {
                skipped.Add(new CopySkip(file.Source, "no access: " + ex.Message));
            }
            catch (IOException ex)
            {
                TryDelete(target);
                skipped.Add(new CopySkip(file.Source, "in use or unreadable: " + ex.Message));
            }

            bytesDone += file.Size;
        }

        Report(files.Count, null, force: true);
        return new CopyReport(copied, bytesCopied, skipped, Cancelled: false);

        void Report(int filesDone, string? current, bool force, long extra = 0)
        {
            if (progress is null || (!force && watch.Elapsed - lastReport < ReportInterval))
            {
                return;
            }

            lastReport = watch.Elapsed;
            progress.Report(new CopyProgress(filesDone, files.Count, bytesDone + extra, total, current, watch.Elapsed));
        }
    }

    /// <summary>Copies one file; returns the bytes written. The copy keeps the source's last-write time and attributes.</summary>
    private static long CopyOne(string source, string target, byte[] buffer, Action<long> onProgress, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        long written = 0;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
        {
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                written += read;
                onProgress(written);
            }
        }

        File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
        File.SetAttributes(target, info.Attributes & ~(FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.ReparsePoint));
        return written;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless.
        }
    }
}
