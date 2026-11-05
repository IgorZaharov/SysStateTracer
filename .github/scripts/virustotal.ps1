# VirusTotal scan of a release file (release workflow).
#
#   virustotal.ps1 -File dist/SysStateTracer.exe              uploads the file, waits for the analysis and writes
#                                                              detected / total / url to $GITHUB_OUTPUT
#   virustotal.ps1 -Readme README.md -Detected 0 -Total 66 -Url <report>
#                                                              puts the badge on the README line marked <!-- virustotal -->
#
# The API key comes from the VIRUSTOTAL_API_KEY environment variable (repository secret). Without it the scan is
# skipped with a warning, so a release never fails only because VirusTotal is not set up or not reachable.
# The free API allows 4 requests a minute: the analysis is polled once a minute, for at most -TimeoutMinutes.
[CmdletBinding(DefaultParameterSetName = 'Scan')]
param(
    [Parameter(ParameterSetName = 'Scan', Mandatory)] [string] $File,
    [Parameter(ParameterSetName = 'Scan')] [int] $TimeoutMinutes = 30,
    [Parameter(ParameterSetName = 'Readme', Mandatory)] [string] $Readme,
    [Parameter(ParameterSetName = 'Readme', Mandatory)] [int] $Detected,
    [Parameter(ParameterSetName = 'Readme', Mandatory)] [int] $Total,
    [Parameter(ParameterSetName = 'Readme', Mandatory)] [string] $Url
)

$ErrorActionPreference = 'Stop'
# The badge line ends with this marker. The badge comes first on the line: a line that starts with "<!--" is an
# HTML block in Markdown, which would show the badge as plain text (1.3.0).
$Marker = '<!-- virustotal -->'

function Get-Badge([int] $detected, [int] $total, [string] $url) {
    # Shields escapes: '/' as %2F, space as %20; green when nothing is detected, orange otherwise.
    $color = if ($detected -eq 0) { '2ea44f' } else { 'e36209' }
    $text = "VirusTotal: $detected/$total Detected"
    "[![$text](https://img.shields.io/badge/VirusTotal-$detected%2F$total%20Detected-$color" +
        "?style=for-the-badge&logo=virustotal&logoColor=white)]($url)"
}

function Update-Readme([string] $path, [string] $badge) {
    $text = [IO.File]::ReadAllText($path)
    # One line, without its line break (CRLF or LF): in .NET, $ matches only before \n.
    $pattern = '(?m)^[^\r\n]*' + [regex]::Escape($Marker) + '[^\r\n]*(?=\r?$)'
    if ($text -notmatch $pattern) { throw "$path has no line with $Marker." }
    $updated = [regex]::Replace($text, $pattern, { param($m) "$badge $Marker" })
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
    return $updated -ne $text
}

if ($PSCmdlet.ParameterSetName -eq 'Readme') {
    $changed = Update-Readme $Readme (Get-Badge $Detected $Total $Url)
    Write-Host ($(if ($changed) { "README updated: VirusTotal $Detected/$Total" } else { 'README already up to date' }))
    exit 0
}

function Write-Output-Value([string] $name, [string] $value) {
    if ($env:GITHUB_OUTPUT) { "$name=$value" >> $env:GITHUB_OUTPUT }
}

$key = $env:VIRUSTOTAL_API_KEY
if ([string]::IsNullOrWhiteSpace($key)) {
    Write-Host '::warning title=VirusTotal::VIRUSTOTAL_API_KEY is not set: the exe was not scanned.'
    Write-Output-Value 'scanned' 'false'
    exit 0
}

$headers = @{ 'x-apikey' = $key; accept = 'application/json' }
$sha256 = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
$report = "https://www.virustotal.com/gui/file/$sha256"

function Invoke-VT([string] $method, [string] $uri, $form = $null) {
    for ($attempt = 1; ; $attempt++) {
        try {
            if ($form) { return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Form $form -TimeoutSec 900 }
            return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 120
        }
        catch {
            $status = [int]($_.Exception.Response.StatusCode)
            # 429: over the per-minute quota; 5xx: VirusTotal busy. Wait a minute and try again, a few times.
            if ($attempt -lt 5 -and ($status -eq 429 -or $status -ge 500 -or $status -eq 0)) {
                Write-Host "VirusTotal answered $status; trying again in a minute."
                Start-Sleep -Seconds 60
                continue
            }

            throw
        }
    }
}

try {
    # Files over 32 MB (the exe is about 120 MB) go to a one-time upload address.
    $size = (Get-Item $File).Length
    $uploadUri = if ($size -gt 32MB) { (Invoke-VT GET 'https://www.virustotal.com/api/v3/files/upload_url').data } else { 'https://www.virustotal.com/api/v3/files' }
    Write-Host "Uploading $File ($([math]::Round($size / 1MB, 1)) MB) to VirusTotal"
    # curl, not Invoke-RestMethod -Form: PowerShell sends the form chunked, without a length, and the upload address
    # for large files answers that with 400 Bad Request (release 1.3.0).
    $answer = Join-Path ([IO.Path]::GetTempPath()) 'virustotal-upload.json'
    $code = curl.exe --silent --show-error --max-time 900 --output $answer --write-out '%{http_code}' `
        --header "x-apikey: $key" --form "file=@$((Resolve-Path $File).Path)" $uploadUri
    $body = if (Test-Path $answer) { Get-Content $answer -Raw } else { '' }
    $analysis = $null
    if ($code -eq '409' -and $body -match 'AlreadySubmittedError') {
        # The same file is still being scanned from an earlier upload (5 Nov 2026): wait for the file's result.
        Write-Host 'This file is already being scanned; waiting for its result.'
    }
    elseif ($LASTEXITCODE -ne 0 -or $code -ne '200') { throw "upload answered $code $body" }
    else { $analysis = ($body | ConvertFrom-Json).data.id }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $status = $null
    $stats = $null
    do {
        Start-Sleep -Seconds 60
        try {
            if ($analysis) {
                $result = Invoke-VT GET "https://www.virustotal.com/api/v3/analyses/$analysis"
                $status = $result.data.attributes.status
                if ($status -eq 'completed') { $stats = $result.data.attributes.stats }
            }
            else {
                $latest = (Invoke-VT GET "https://www.virustotal.com/api/v3/files/$sha256").data.attributes.last_analysis_stats
                $engines = if ($latest) { [int]$latest.malicious + [int]$latest.suspicious + [int]$latest.undetected + [int]$latest.harmless } else { 0 }
                if ($engines -gt 0) { $stats = $latest; $status = 'completed' } else { $status = 'queued' }
            }
        }
        catch {
            # Right after an upload the analysis (or the file) can be unknown for a while (404, seen on 5 Nov 2026).
            if ([int]($_.Exception.Response.StatusCode) -ne 404) { throw }
            $status = 'not found yet'
        }

        Write-Host "Analysis: $status"
    } while ($status -ne 'completed' -and (Get-Date) -lt $deadline)

    if ($null -eq $stats) {
        # No finished analysis: the file's latest result, when VirusTotal has one (a file it scanned before).
        try {
            $file = Invoke-VT GET "https://www.virustotal.com/api/v3/files/$sha256"
            $stats = $file.data.attributes.last_analysis_stats
            if ($stats) { Write-Host 'Using the latest analysis of this file.' }
        }
        catch {
            $stats = $null
        }
    }

    if ($null -eq $stats) {
        Write-Host "::warning title=VirusTotal::The analysis did not finish within $TimeoutMinutes minutes: $report"
        Write-Output-Value 'scanned' 'false'
        exit 0
    }

    # Detected = engines that call it malicious, as the VirusTotal page counts them; the total leaves out engines
    # that could not scan this file type, timed out or failed.
    $detected = [int]$stats.malicious
    $total = [int]$stats.malicious + [int]$stats.suspicious + [int]$stats.undetected + [int]$stats.harmless
    $level = if ($detected -eq 0) { 'notice' } else { 'warning' }
    Write-Host "::$level title=VirusTotal::$detected/$total detected ($([int]$stats.suspicious) suspicious): $report"
    Write-Output-Value 'scanned' 'true'
    Write-Output-Value 'detected' "$detected"
    Write-Output-Value 'total' "$total"
    Write-Output-Value 'url' $report
}
catch {
    Write-Host "::warning title=VirusTotal::The scan failed: $($_.Exception.Message)"
    Write-Output-Value 'scanned' 'false'
    exit 0
}
