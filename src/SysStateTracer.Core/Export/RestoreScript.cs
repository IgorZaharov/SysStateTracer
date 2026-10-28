using System.Text;

namespace SysStateTracer.Core.Export;

/// <summary>
/// The PowerShell script written into .\Files (F7): copies everything in that folder back to where it came from.
/// It reads the folder layout rather than a list, so files from earlier exports into the same folder are included:
/// files from the system drive sit directly in .\Files ("ProgramData\App\x" → "C:\ProgramData\App\x"), files from
/// other drives under "_D", "_E", … ("_D\Data\x" → "D:\Data\x"), as decision D4 lays them out.
/// </summary>
public static class RestoreScript
{
    public const string FileName = "Restore-Files.ps1";

    /// <summary>Writes the script into <paramref name="filesFolder"/>; returns its path.</summary>
    public static string Write(string filesFolder, char? systemDrive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesFolder);
        Directory.CreateDirectory(filesFolder);
        var path = Path.Combine(filesFolder, FileName);

        // UTF-8 with a byte order mark, so Windows PowerShell 5.1 reads non-ASCII paths correctly.
        File.WriteAllText(path, Text(systemDrive ?? Path.GetPathRoot(Environment.SystemDirectory)![0]), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    public static string Text(char systemDrive)
    {
        var drive = char.ToUpperInvariant(systemDrive);
        return $$"""
            <#
            .SYNOPSIS
                Copies the files in this folder back to the places SysStateTracer copied them from.

            .DESCRIPTION
                Written by SysStateTracer next to the files it copies (/export, or "Copy to .\Files" in the
                comparison window). The folders keep the original structure:
                  - files from the system drive are directly in this folder:
                      ProgramData\App\settings.ini  ->  {{drive}}:\ProgramData\App\settings.ini
                  - files from other drives are under _D, _E, ...:
                      _D\Data\report.xlsx            ->  D:\Data\report.xlsx
                Files at the destination are overwritten. Folders that are missing are created.
                Run it from PowerShell opened with "Run as administrator" to restore files in protected folders.

            .PARAMETER SystemDrive
                The drive the files directly in this folder came from ({{drive}}: when this script was written).

            .EXAMPLE
                .\Restore-Files.ps1 -WhatIf
                Lists what would be copied, without copying anything.

            .EXAMPLE
                .\Restore-Files.ps1
                Copies every file back.
            #>
            [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
            param(
                [string] $SystemDrive = '{{drive}}:'
            )

            $ErrorActionPreference = 'Stop'
            $root = $PSScriptRoot.TrimEnd('\')
            $copied = 0
            $failed = 0

            foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
                if ($file.FullName -eq $PSCommandPath) { continue }
                $relative = $file.FullName.Substring($root.Length + 1)
                $parts = $relative.Split([char]'\', 2)
                if ($parts.Count -eq 2 -and $parts[0] -match '^_([A-Za-z])$') {
                    $target = '{0}:\{1}' -f $Matches[1].ToUpperInvariant(), $parts[1]
                }
                else {
                    $target = '{0}\{1}' -f $SystemDrive.TrimEnd('\'), $relative
                }

                if ($PSCmdlet.ShouldProcess($target, "Copy $relative")) {
                    try {
                        $folder = Split-Path -Parent $target
                        if (-not (Test-Path -LiteralPath $folder)) {
                            New-Item -ItemType Directory -Path $folder -Force | Out-Null
                        }

                        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
                        $copied++
                    }
                    catch {
                        Write-Warning ('{0}: {1}' -f $target, $_.Exception.Message)
                        $failed++
                    }
                }
            }

            Write-Host ('{0} files copied back, {1} could not be copied.' -f $copied, $failed)
            if ($failed -gt 0) { exit 1 }

            """;
    }
}
