# Runs a command, streams its output, and on failure turns compiler/MSBuild/NuGet
# error and warning lines into GitHub annotations, so failures are readable from the
# run's summary page and the API without downloading the full log.
#
# Usage: pwsh .github/scripts/run.ps1 dotnet build SysStateTracer.slnx -c Release

$exe = $args[0]
$rest = @($args | Select-Object -Skip 1)

$lines = [System.Collections.Generic.List[string]]::new()
& $exe @rest 2>&1 | ForEach-Object {
    $line = "$_"
    $lines.Add($line)
    Write-Host $line
}
$code = $LASTEXITCODE

if ($code -ne 0) {
    $problems = $lines |
        Where-Object { $_ -match '\b(error|warning)\s+[A-Z]{2,}\d+' -or $_ -match '\berror\b.*:' } |
        ForEach-Object { $_.Trim() } |
        Select-Object -Unique -First 40

    if (-not $problems) {
        $problems = $lines | Select-Object -Last 15
    }

    foreach ($p in $problems) {
        $msg = $p.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error::$msg"
    }
}

exit $code
