<#
.SYNOPSIS
    Summarises a .trx test results file as GitHub Actions annotations (CI helper).

.DESCRIPTION
    Groups failed tests by their first error line and posts, per group, the test names, the
    message and the top stack frames. Annotations are readable through the GitHub API
    (check-runs/<job>/annotations) even when raw job logs aren't. GitHub keeps at most 10
    error annotations per step; this emits at most 9, packing several groups into each.

.EXAMPLE
    ./tools/ci-test-summary.ps1 TestResults/tests.trx
#>
param(
    [Parameter(Mandatory)] [string]$Path,
    [int]$StackFrames = 6,
    [int]$LinesPerAnnotation = 70
)

function Send-Annotation([string]$Level, [string]$Title, [string[]]$Lines) {
    if (-not $Lines -or $Lines.Count -eq 0) { return }
    $text = ($Lines -join "`n") -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
    Write-Host "::$Level title=$Title::$text"
}

$file = Get-ChildItem -Path $Path -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $file) {
    Write-Host "::error title=Test summary::No results file at $Path"
    exit 0
}

$raw = [IO.File]::ReadAllText($file.FullName)
Write-Host "::notice title=Test results file::$($file.Name), $([math]::Round($file.Length / 1MB, 1)) MB"

# Test output can be huge (a chatty test writes megabytes); keep the last part of each
# StdOut block so the XML stays small. Cuts happen at line breaks, outside XML entities.
$big = New-Object System.Collections.Generic.List[string]
$raw = [regex]::Replace($raw, '(?s)<StdOut>(.*?)</StdOut>', {
    param($m)
    $text = $m.Groups[1].Value
    if ($text.Length -le 40000) { return $m.Value }
    $cut = $text.IndexOf("`n", $text.Length - 40000)
    if ($cut -lt 0) { return '<StdOut></StdOut>' }
    $at = $raw.LastIndexOf('testName="', $m.Index)
    $name = if ($at -ge 0) { $raw.Substring($at + 10, [Math]::Max(0, $raw.IndexOf('"', $at + 10) - $at - 10)) } else { '?' }
    $big.Add("$name ($([math]::Round($text.Length / 1MB, 1)) MB)")
    return '<StdOut>' + $text.Substring($cut + 1) + '</StdOut>'
})
if ($big.Count -gt 0) {
    Write-Host "::notice title=Test output trimmed::Kept the last 40 kB of output of: $(($big | Select-Object -First 10) -join ', ')"
}
[xml]$trx = $raw
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$results = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node })
$failed = @($results | Where-Object { $_.outcome -eq 'Failed' })
$passed = @($results | Where-Object { $_.outcome -eq 'Passed' })

Send-Annotation 'notice' 'Test totals' @("$($results.Count) tests: $($passed.Count) passed, $($failed.Count) failed")

# Reports: tests write "[notice] ..." lines (timings, measurements) that should be visible
# whether they pass or not. One annotation for all of them, so they don't crowd out failures.
$reports = New-Object System.Collections.Generic.List[string]
foreach ($r in $results) {
    foreach ($l in @("$($r.Output.StdOut)" -split "`n")) {
        $t = $l.Trim()
        if ($t -like '`[notice`]*') { $reports.Add("[$($r.testName)] " + $t.Substring(8).Trim()) }
    }
}
if ($reports.Count -gt 0) { Send-Annotation 'notice' 'Test reports' @($reports | Select-Object -First 60) }

if ($failed.Count -eq 0) { exit 0 }

$groups = $failed | ForEach-Object {
    $msg = "$($_.Output.ErrorInfo.Message)".Trim()
    $first = ($msg -split "`n")[0].Trim()
    $stack = @("$($_.Output.ErrorInfo.StackTrace)" -split "`n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -like 'at *' -and $_ -notmatch 'System\.(Reflection|RuntimeMethodHandle)|InvokeStub_|NUnit\.' } |
        Select-Object -First $StackFrames)
    # Warnings/errors the test driver echoed from the ReBuzz log ("[log ...]" lines).
    $log = @("$($_.Output.StdOut)" -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -like '`[log *' } | Select-Object -First 6)
    [pscustomobject]@{ Test = $_.testName; First = $first; Message = $msg; Stack = $stack; Log = $log }
} | Group-Object First | Sort-Object Count -Descending

$lines = New-Object System.Collections.Generic.List[string]
foreach ($g in $groups) {
    $sample = $g.Group[0]
    if ($g.Count -eq 1) { $lines.Add("## 1x [$($sample.Test)]: $($g.Name)") } else { $lines.Add("## $($g.Count)x: $($g.Name)") }
    if ($g.Count -le 2) {
        # Small groups are usually the interesting ones. Their messages (assertion dumps) run
        # over many short lines, so flatten them and keep both ends: expected and actual.
        $flat = (($sample.Message -split "`n" | Select-Object -Skip 1) -join ' ') -replace '\s+', ' '
        if ($flat.Length -gt 1800) { $flat = $flat.Substring(0, 900) + ' ... ' + $flat.Substring($flat.Length - 900) }
        for ($k = 0; $k -lt $flat.Length; $k += 180) { $lines.Add('   ' + $flat.Substring($k, [Math]::Min(180, $flat.Length - $k))) }
    } else {
        $extra = @($sample.Message -split "`n" | Select-Object -Skip 1 | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 4)
        foreach ($l in $extra) { $lines.Add("   $l") }
    }
    foreach ($s in $sample.Stack) { $lines.Add("   $s") }
    foreach ($l in $sample.Log) { $lines.Add("   $l") }
    $names = @($g.Group | ForEach-Object { $_.Test } | Sort-Object -Unique)
    $shown = @($names | Select-Object -First 12)
    $lines.Add("   tests: " + ($shown -join ', ') + $(if ($names.Count -gt $shown.Count) { " (+$($names.Count - $shown.Count) more)" } else { '' }))
    $lines.Add('')
}

$part = 1
for ($i = 0; $i -lt $lines.Count -and $part -le 8; $i += $LinesPerAnnotation) {
    $chunk = $lines[$i..([Math]::Min($i + $LinesPerAnnotation, $lines.Count) - 1)]
    Send-Annotation 'error' "Test failures $part" $chunk
    $part++
}
