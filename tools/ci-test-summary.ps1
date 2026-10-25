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

[xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$results = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node })
$failed = @($results | Where-Object { $_.outcome -eq 'Failed' })
$passed = @($results | Where-Object { $_.outcome -eq 'Passed' })

Send-Annotation 'notice' 'Test totals' @("$($results.Count) tests: $($passed.Count) passed, $($failed.Count) failed")
if ($failed.Count -eq 0) { exit 0 }

$groups = $failed | ForEach-Object {
    $msg = "$($_.Output.ErrorInfo.Message)".Trim()
    $first = ($msg -split "`n")[0].Trim()
    $stack = @("$($_.Output.ErrorInfo.StackTrace)" -split "`n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -like 'at *' -and $_ -notmatch 'System\.(Reflection|RuntimeMethodHandle)|InvokeStub_|NUnit\.' } |
        Select-Object -First $StackFrames)
    [pscustomobject]@{ Test = $_.testName; First = $first; Message = $msg; Stack = $stack }
} | Group-Object First | Sort-Object Count -Descending

$lines = New-Object System.Collections.Generic.List[string]
foreach ($g in $groups) {
    $lines.Add("## $($g.Count)x: $($g.Name)")
    $sample = $g.Group[0]
    $extra = @($sample.Message -split "`n" | Select-Object -Skip 1 | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 4)
    foreach ($l in $extra) { $lines.Add("   $l") }
    foreach ($s in $sample.Stack) { $lines.Add("   $s") }
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
