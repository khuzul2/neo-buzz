<#
.SYNOPSIS
    Downloads prebuilt machine DLLs from the GitHub releases listed under
    "releaseBinaries" in ExternalMachines\machines.json.

.DESCRIPTION
    For each repo it takes the latest release and downloads assets matching the entry's
    assetPattern (*NET.dll machines, .prs presets, and .zip which is unpacked). Files go to
    ExternalMachines\binaries\repos\<owner>__<repo>\<gearFolder>. Existing files are left
    alone unless -Force is given. Each run appends what it fetched (tag, file, SHA-256) to
    ExternalMachines\binaries\FETCHED.md so the result can be reviewed before committing.

    build.ps1 uses a repo's binaries only when that machine did not build from source in
    the same run, so they are a fallback for broken source builds.

    Unauthenticated GitHub API calls are limited to 60 per hour; set $env:GITHUB_TOKEN to
    raise that.

.EXAMPLE
    .\tools\fetch-machine-binaries.ps1
.EXAMPLE
    .\tools\fetch-machine-binaries.ps1 -Repo thepedal/pedal-juno106 -Force
#>
[CmdletBinding()]
param(
    [string[]]$Repo,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar on 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$ExtRoot = [IO.Path]::Combine($Root, 'ExternalMachines')
$BinRoot = [IO.Path]::Combine($ExtRoot, 'binaries')
$LogFile = [IO.Path]::Combine($BinRoot, 'FETCHED.md')
$Temp = [IO.Path]::Combine([IO.Path]::GetTempPath(), "rebuzz-fetch-$([guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Path $Temp | Out-Null

$headers = @{ 'User-Agent' = 'rebuzz-fork-fetch'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }

$manifest = Get-Content -LiteralPath ([IO.Path]::Combine($ExtRoot, 'machines.json')) -Raw | ConvertFrom-Json
$entries = @($manifest.releaseBinaries)
if ($Repo) { $entries = @($entries | Where-Object { $Repo -contains $_.repo }) }

$rows = New-Object System.Collections.Generic.List[string]
$problems = New-Object System.Collections.Generic.List[string]

function Test-Wanted([string]$Name, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Name -like $p) { return $true } }
    return $false
}

try {
    foreach ($e in $entries) {
        $slug = $e.repo -replace '/', '__'
        $patterns = $e.assetPattern -split '\|'
        $dest = [IO.Path]::Combine($BinRoot, 'repos', $slug, $e.gearFolder)
        try {
            $rel = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$($e.repo)/releases/latest"
        } catch {
            Write-Host "-- $($e.repo): no release ($($_.Exception.Message))" -ForegroundColor DarkGray
            continue
        }
        Write-Host "-- $($e.repo) $($rel.tag_name)"
        $repoTemp = [IO.Path]::Combine($Temp, $slug)
        New-Item -ItemType Directory -Path $repoTemp -Force | Out-Null
        foreach ($asset in @($rel.assets)) {
            if (-not (Test-Wanted $asset.name $patterns)) { continue }
            try {
                $file = [IO.Path]::Combine($repoTemp, $asset.name)
                Invoke-WebRequest -Headers @{ 'User-Agent' = 'rebuzz-fork-fetch' } -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing
                if ($asset.name -like '*.zip') {
                    $unz = [IO.Path]::Combine($repoTemp, "unzip-$([guid]::NewGuid().ToString('N'))")
                    Expand-Archive -LiteralPath $file -DestinationPath $unz -Force
                    $candidates = @(Get-ChildItem -LiteralPath $unz -Recurse -File |
                        Where-Object { Test-Wanted $_.Name @('*NET.dll', '*.prs', '*.prs.xml') })
                } else {
                    $candidates = @(Get-Item -LiteralPath $file)
                }
                if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
                foreach ($c in $candidates) {
                    $target = [IO.Path]::Combine($dest, $c.Name)
                    if ((Test-Path -LiteralPath $target) -and -not $Force) {
                        Write-Host "   skip $($c.Name) (exists)"
                        continue
                    }
                    Copy-Item -LiteralPath $c.FullName -Destination $target -Force
                    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                    $rows.Add("| $($e.repo) | $($rel.tag_name) | $($c.Name) | $($e.gearFolder) | ``$hash`` |")
                    Write-Host "   + $($c.Name)"
                }
            } catch {
                $msg = "$($e.repo) $($asset.name): $($_.Exception.Message)"
                $problems.Add($msg)
                Write-Warning $msg
            }
        }
    }
} finally {
    Remove-Item -LiteralPath $Temp -Recurse -Force -ErrorAction SilentlyContinue
    # Append this run's results, even when it stopped early.
    $section = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $LogFile)) {
        $section.Add('# Fetched machine binaries')
        $section.Add('')
        $section.Add('Written by tools/fetch-machine-binaries.ps1. Review before committing.')
    }
    $section.Add('')
    $section.Add("## $((Get-Date).ToString('yyyy-MM-dd HH:mm'))")
    $section.Add('')
    if ($rows.Count -gt 0) {
        $section.Add('| Repo | Release | File | Gear folder | SHA-256 |')
        $section.Add('|---|---|---|---|---|')
        $section.AddRange($rows)
    } else {
        $section.Add('Nothing new fetched.')
    }
    foreach ($p in $problems) { $section.Add("- Problem: $p") }
    Add-Content -LiteralPath $LogFile -Value $section -Encoding UTF8
}

Write-Host "Done: $($rows.Count) file(s) fetched, $($problems.Count) problem(s). See ExternalMachines\binaries\FETCHED.md" -ForegroundColor Green
