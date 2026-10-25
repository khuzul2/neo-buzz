<#
.SYNOPSIS
    Turns a build log into GitHub Actions error annotations (CI helper).

.DESCRIPTION
    Annotations can be read through the GitHub API (check-runs/<job>/annotations), while raw
    job logs need extra access. On a failed build this posts the log's error-looking lines
    and its last lines as a handful of annotations, so a failure can be diagnosed from the
    API alone. GitHub keeps at most 10 error annotations per step; this emits at most 8.

.EXAMPLE
    ./tools/ci-annotate-log.ps1 build.log
#>
param(
    [Parameter(Mandatory)] [string]$Path,
    [int]$TailLines = 100
)

function Send-Annotation([string]$Title, [string[]]$Lines) {
    if (-not $Lines -or $Lines.Count -eq 0) { return }
    $text = ($Lines -join "`n") -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
    Write-Host "::error title=$Title::$text"
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "::error title=Build log::No log found at $Path"
    exit 0
}

$lines = @(Get-Content -LiteralPath $Path | ForEach-Object { $_.TrimEnd() } | Where-Object { $_ })
$pattern = '(?i)(\berror\b|exception|\bfailed\b|cannot find|could not|not found|MSB\d{4})'
$errors = @($lines | Where-Object { $_ -match $pattern -and $_ -notmatch '(?i)warning' } | Select-Object -Unique)

$chunk = 20
for ($i = 0; $i -lt [Math]::Min($errors.Count, 3 * $chunk); $i += $chunk) {
    $part = $errors[$i..([Math]::Min($i + $chunk, $errors.Count) - 1)]
    Send-Annotation "Build errors $($i + 1)-$($i + $part.Count) of $($errors.Count)" $part
}

$tail = @($lines | Select-Object -Last $TailLines)
$chunk = 25
for ($i = 0; $i -lt $tail.Count; $i += $chunk) {
    $part = $tail[$i..([Math]::Min($i + $chunk, $tail.Count) - 1)]
    Send-Annotation "Build log tail $([int]($i / $chunk) + 1)" $part
}
