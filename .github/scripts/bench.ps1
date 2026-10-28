# Runs one benchmark snapshot and turns its RESULT/INFO/MEMORY/SKIPPED lines into notices.
# GitHub shows at most 10 notices per step, so each snapshot runs in its own step.
param(
    [Parameter(Mandatory)] [string] $Label,
    [string] $Command = 'snapshot',
    [string] $Parallel = '0',
    [string[]] $Extra = @()
)

$out = Join-Path $env:RUNNER_TEMP 'bench'
$lines = if ($Command -eq 'compare') {
    & dotnet run --project tests/SysStateTracer.Benchmarks -c Release --no-build -- compare $out --label "p$Parallel-$Label" 2>&1
} else {
    & dotnet run --project tests/SysStateTracer.Benchmarks -c Release --no-build -- snapshot $out --label "p$Parallel-$Label" --parallel $Parallel @Extra 2>&1
}
$code = $LASTEXITCODE
$lines | ForEach-Object { Write-Host $_ }
foreach ($line in $lines) {
    if ("$line" -match '^(RESULT|INFO|MEMORY|SKIPPED): (.*)$') {
        $text = $Matches[2]
        if ($text.Length -gt 3000) { $text = $text.Substring(0, 3000) }
        Write-Host ('::notice title={0}::{1}' -f $Matches[1], $text)
    }
}

if ($code -ne 0) {
    Write-Host "::error title=$Label::benchmark exited with $code"
    exit 1
}
