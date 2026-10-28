# Turns the .trx test results into annotations: failures as errors (logs are not always reachable),
# printed measurements as notices, and a crash of the UI test process as an error.
try {
  $trx = @(Get-ChildItem TestResults -Filter *.trx -Recurse -ErrorAction SilentlyContinue)
  $total = 0; $passed = 0; $withOutput = 0
  foreach ($file in $trx) {
    $doc = [System.Xml.XmlDocument]::new()
    $doc.Load($file.FullName)
    foreach ($result in $doc.GetElementsByTagName('UnitTestResult')) {
      $total++
      if ($result.GetAttribute('outcome') -eq 'Passed') { $passed++ }
      elseif ($result.GetAttribute('outcome') -eq 'Failed') {
        # Failure details as annotations: run logs are not always reachable
        $message = ($result.GetElementsByTagName('Message') | Select-Object -First 1).InnerText
        $stack = ($result.GetElementsByTagName('StackTrace') | Select-Object -First 1).InnerText
        $where = ($stack -split "`r?`n" | Where-Object { $_ -match ' in .*:line \d+' } | Select-Object -First 2) -join ' | '
        $text = (($message -split "`r?`n") -join ' | ') + ' @ ' + $where.Trim()
        if ($text.Length -gt 3000) { $text = $text.Substring(0, 3000) }
        Write-Host ('::error title=FAILED {0}::{1}' -f $result.GetAttribute('testName'), $text)
      }
      $outputs = $result.GetElementsByTagName('StdOut')
      if ($outputs.Count -gt 0 -and $outputs[0].InnerText.Trim().Length -gt 0) {
        $withOutput++
        $test = ($result.GetAttribute('testName') -split '\.')[-1]
        $lines = $outputs[0].InnerText -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }
        $text = $lines -join ' | '
        Write-Host ('::notice title={0}::{1}' -f $test, $text)
      }
    }
  }
  Write-Host ('::notice title=Tests::{0} of {1} passed in {2} result files; {3} printed measurements' -f $passed, $total, $trx.Count, $withOutput)
  if (Test-Path ui-crash.txt) {
    $crash = ((Get-Content ui-crash.txt -Raw) -split "`r?`n" | Where-Object { $_.Trim() }) -join ' | '
    if ($crash.Length -gt 3000) { $crash = $crash.Substring(0, 3000) }
    Write-Host "::error title=UI test process crashed::$crash"
  }
}
catch {
  Write-Host ('::warning title=Report step::{0} (line {1})' -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber)
}
exit 0
