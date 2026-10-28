# End-to-end check of the published SysStateTracer.exe on a clean runner (plan Step 55):
# snapshot -> change -> snapshot -> compare -> export, all through the command line, plus a
# Microsoft Defender scan of the exe when Defender is available. Results become annotations.
#
# Usage: pwsh .github/scripts/smoke.ps1 <path to SysStateTracer.exe>

param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
$os = (Get-CimInstance Win32_OperatingSystem)
$osName = "$($os.Caption) $($os.Version)"
$root = Join-Path $env:RUNNER_TEMP ("smoke-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$app = New-Item -ItemType Directory (Join-Path $root 'app')
$data = New-Item -ItemType Directory (Join-Path $root 'data')
Copy-Item $Exe $app
$exePath = Join-Path $app 'SysStateTracer.exe'
$key = 'HKCU:\Software\SysStateTracerSmoke'
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Cli([string] $arguments) {
    $out = Join-Path $root 'out.txt'
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $exePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    [pscustomobject]@{ Code = $p.ExitCode; Text = (Get-Content $out -Raw); Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
}

function Check([bool] $ok, [string] $what) {
    if (-not $ok) { $failures.Add($what) }
}

try {
    # Scope: one folder and one HKCU key, so the run is quick; services included.
    $settings = @{
        formatVersion    = 2
        ignoreListSource = 'Local'
        scope            = @{ fileSystemRoots = @("$data"); registryRoots = @('HKCU\Software\SysStateTracerSmoke'); includeServices = $true; includeDrivers = $false; excluded = @() }
    } | ConvertTo-Json -Depth 4
    Set-Content (Join-Path $app 'SysStateTracer.config') $settings -Encoding utf8
    Set-Content (Join-Path $app 'SysStateTracer.ignore.txt') "# smoke test: no rules" -Encoding utf8

    New-Item $key -Force | Out-Null
    New-ItemProperty $key -Name 'Version' -Value '1.0' -PropertyType String -Force | Out-Null
    Set-Content (Join-Path $data 'readme.txt') 'one'

    $first = Invoke-Cli '/new-snap "Before app install"'
    Check ($first.Code -eq 0) "first /new-snap exit $($first.Code): $($first.Text)"

    New-Item -ItemType Directory (Join-Path $data 'App') | Out-Null
    Set-Content (Join-Path $data 'App\app.exe') 'binary'
    Set-Content (Join-Path $data 'readme.txt') 'one two three'
    New-ItemProperty $key -Name 'Version' -Value '2.0' -PropertyType String -Force | Out-Null
    New-ItemProperty $key -Name 'Installed' -Value 1 -PropertyType DWord -Force | Out-Null

    $second = Invoke-Cli '/new-snap "After app install"'
    Check ($second.Code -eq 0) "second /new-snap exit $($second.Code): $($second.Text)"

    $compare = Invoke-Cli '/compare-snap "Before app install" "After app install" "Install changes.html"'
    Check ($compare.Code -eq 0) "/compare-snap exit $($compare.Code): $($compare.Text)"
    $report = Join-Path $app 'Install changes.html'
    Check (Test-Path $report) 'report not written'
    if (Test-Path $report) {
        $html = Get-Content $report -Raw
        Check ($html -match 'app\.exe') 'report does not list app.exe'
        Check ($html -match 'Installed') 'report does not list the new registry value'
    }

    $export = Invoke-Cli '/export "Before app install" "After app install"'
    Check ($export.Code -eq 0) "/export exit $($export.Code): $($export.Text)"
    $reg = Join-Path $app 'Export_Before app install_vs_After app install.reg'
    Check (Test-Path $reg) '.reg not written'
    if (Test-Path $reg) {
        $text = Get-Content $reg -Raw -Encoding Unicode
        Check ($text -match '"Installed"=dword:00000001') '.reg lacks the new value'
        Check ($text -match '"Version"="2.0"') '.reg lacks the changed value'
    }

    # Files keeps the folder structure: no drive letter on the system drive, "_D\..." on other drives.
    $relative = if ($data.FullName.StartsWith($env:SystemDrive, [StringComparison]::OrdinalIgnoreCase)) {
        $data.FullName.Substring(3)
    } else {
        '_' + $data.FullName.Substring(0, 1).ToUpperInvariant() + '\' + $data.FullName.Substring(3)
    }
    Check (Test-Path (Join-Path $app "Files\$relative\App\app.exe")) 'new file not copied to Files'
    Check (Test-Path (Join-Path $app "Files\$relative\readme.txt")) 'modified file not copied to Files'

    $rename = Invoke-Cli '/rename-snap "After app install" "App installed"'
    Check ($rename.Code -eq 0) "/rename-snap exit $($rename.Code)"
    $delete = Invoke-Cli '/delete-snap'
    Check ($delete.Code -eq 0 -and $delete.Text -match 'App installed') "/delete-snap did not delete the newest: $($delete.Text)"

    # Microsoft Defender, when the runner has it.
    $defender = 'not available on this runner'
    try {
        $status = Get-MpComputerStatus -ErrorAction Stop
        if ($status.AntivirusEnabled) {
            Start-MpScan -ScanType CustomScan -ScanPath $exePath -ErrorAction Stop
            $threats = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.Resources -match 'SysStateTracer' })
            $defender = if ($threats.Count -eq 0) { "no detection (engine $($status.AMEngineVersion), signatures $($status.AntivirusSignatureVersion))" } else { "DETECTED: $($threats.ThreatID -join ', ')" }
            Check ($threats.Count -eq 0) "Defender detection: $defender"
        }
        else {
            $defender = 'installed but real-time protection is off on this runner'
        }
    }
    catch {
        $defender = "not available on this runner ($($_.Exception.Message))"
    }

    $summary = "$osName | first snapshot $($first.Seconds) s, second $($second.Seconds) s, compare $($compare.Seconds) s, export $($export.Seconds) s | Defender: $defender"
    if ($failures.Count -eq 0) {
        Write-Host "::notice title=Clean runner ($osName)::$summary"
    }
    else {
        foreach ($f in $failures) {
            $msg = $f.Replace("`r", ' ').Replace("`n", ' ')
            if ($msg.Length -gt 900) { $msg = $msg.Substring(0, 900) }
            Write-Host "::error title=Clean runner ($osName)::$msg"
        }
        exit 1
    }
}
finally {
    Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue
}
