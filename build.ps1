<#
.SYNOPSIS
    Builds NeoBuzz Live (this fork of ReBuzz) plus every buildable external machine, and packs a
    ready-to-run zip with all machines already in place: dist\NeoBuzz-Live-<version>-x64.zip, whose
    NeoBuzzLive.exe is the app (the apphost of ReBuzz.csproj; its assembly stays ReBuzz.dll).

.DESCRIPTION
    1. Finds Visual Studio (MSBuild + C++ x64 tools) and the .NET 10 SDK.
    2. Initialises git submodules.
    3. Builds ReBuzz.sln (Release|x64) into bin\Release\x64\net10.0-windows10.0.26100.0.
       This includes ReBuzz's own machines and copies RunTime\Gear (73 prebuilt machines).
    4. Builds ExternalMachines: every 'rebuzz-managed' entry in ExternalMachines\machines.json
       (dotnet build), and the classic native machines with sources (the "natives" of its
       Buzztrax entry: FSM, Oomek and Jeskola) as x86 and x64 with tools\build-natives.ps1.
       A rebuilt native replaces upstream's binary only where its entry says "ship": "rebuilt"
       (it renders as upstream's does: tools\native-compare.py) and its licence allows it, or its
       "licenseException" is exactly one of the two recorded below (no licence in its source; user decision).
    5. Merges prebuilt DLLs from ExternalMachines\binaries.
    6. Writes GEAR-INVENTORY.txt, BUILD-INFO.txt, README-FIRST.txt and Licenses\ (NOTICE.txt: each
       machine built from source, its licence and source; the licence texts machines.json's
       "licenseTexts" has for them, and ReBuzz's and its libraries'), then zips to dist\.

    Precedence: upstream machines > machines built from source > prebuilt binaries.
    Nothing ever overwrites a file already in the Gear folder (but the rebuilt natives marked
    "ship": "rebuilt", which replace upstream's binary of the same machine), and "Foo.dll" and
    "Foo.x64.dll" count as the same machine. Files this script added on a previous run are
    removed first, so re-runs (and -SkipCore) start from the upstream Gear set.

    A machine that fails to build is reported and skipped (use -StrictMachines to fail).

    Run from a normal PowerShell prompt on Windows (Windows PowerShell 5.1 or pwsh 7).

.PARAMETER Version
    Version label for the package. Default: yyyy.MM.dd-<git short sha>.

.PARAMETER PlatformToolset
    Override the C++ toolset. The projects ask for v145 (Visual Studio 2026). When an
    older Visual Studio is found and this is not set, v143 is used with a warning.

.PARAMETER SkipCore
    Reuse an existing build (machines + package only).

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -SkipCore
.EXAMPLE
    .\build.ps1 -SkipNativeMachines
.EXAMPLE
    .\build.ps1 -Version 0.1.0-session-mvp
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$PlatformToolset,
    [switch]$SkipCore,
    [switch]$SkipManagedMachines,
    [switch]$SkipNativeMachines,
    [switch]$SkipBinaries,
    [switch]$SkipStudio,
    [switch]$StrictMachines,
    [switch]$NoZip,
    [string]$DistDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = $PSScriptRoot
if ($DistDir) {
    # .NET APIs resolve relative paths against the process directory, not $PWD.
    $DistDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DistDir)
} else {
    $DistDir = [IO.Path]::Combine($Root, 'dist')
}
$Tfm = 'net10.0-windows10.0.26100.0'
$OutDir = [IO.Path]::Combine($Root, 'bin', $Configuration, 'x64', $Tfm)
$ExtRoot = [IO.Path]::Combine($Root, 'ExternalMachines')
$StageRoot = [IO.Path]::Combine($Root, 'build', 'ExternalMachines')
$AddedList = [IO.Path]::Combine($StageRoot, 'added.txt')   # Gear files this script added
$Failures = New-Object System.Collections.Generic.List[string]
$Origins = @{}   # "Generators\Foo.dll" -> origin label, for the inventory
$BuiltRepos = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

# ---------------------------------------------------------------- helpers

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$File' failed with exit code $LASTEXITCODE" }
}

function Get-Prop($Object, [string]$Name) {
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Add-MachineFailure([string]$Text) {
    $Failures.Add($Text)
    if ($StrictMachines) { throw $Text }
    Write-Warning $Text
}

function New-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

# "FSM Infector.x64.dll" and "FSM Infector.dll" are the same machine; compare without ".x64".
function Get-GearKey([string]$Name) {
    return ($Name -replace '\.x64(?=\.)', '').ToLowerInvariant()
}

# Fork (licences): a machine's licence id is how its "license" starts, "-or-later" and "-only" aside
# ("GPL-3.0-or-later (the source's header)" is GPL-3.0, "LGPL-2.1 (FSM/COPYING; ...)" LGPL-2.1).
function Get-LicenseId([string]$License) {
    if (-not $License) { return $null }
    $first = ($License.Trim() -split '[\s;,(]')[0]
    return ($first -replace '(-or-later|-only|\+)$', '')
}

# The text of a licence the package can ship: machines.json's "licenseTexts" entry for the id, its file (under
# ExternalMachines) checked to start as that licence does. $null when the table has none.
function Get-LicenseText([string]$Id) {
    if (-not $Id) { return $null }
    $texts = Get-Prop $manifest 'licenseTexts'
    if (-not $texts) { return $null }
    $t = Get-Prop $texts $Id
    if (-not $t -or -not (Get-Prop $t 'file')) { return $null }
    $path = [IO.Path]::Combine($ExtRoot, ($t.file -replace '/', '\'))
    if (-not (Test-Path -LiteralPath $path)) { throw "machines.json licenseTexts: $Id's text $($t.file) is not in ExternalMachines" }
    $head = (Get-Content -LiteralPath $path -TotalCount 30) -join "`n"
    foreach ($line in @(Get-Prop $t 'check')) {
        if ($line -and -not $head.Contains($line)) { throw "machines.json licenseTexts: $($t.file) doesn't start as $Id does (no '$line')" }
    }
    return [pscustomobject]@{ Id = $Id; Path = $path }
}

# Copies files into a Gear subfolder unless the folder already has them (also under their
# .x64 / non-.x64 name), records their origin, and logs them in added.txt for clean re-runs.
function Copy-IntoGear {
    param([System.IO.FileInfo[]]$Files, [string]$GearFolder, [string]$Origin)
    $dest = [IO.Path]::Combine($OutDir, 'Gear', $GearFolder)
    New-Dir $dest
    $existing = @{}
    Get-ChildItem -LiteralPath $dest -File | ForEach-Object { $existing[(Get-GearKey $_.Name)] = $_.Name }
    $copied = 0
    foreach ($f in $Files) {
        if ($f.Name -eq '.gitkeep') { continue }
        $key = Get-GearKey $f.Name
        if ($existing.ContainsKey($key)) {
            Write-Verbose "skip (already have $($existing[$key])): $GearFolder\$($f.Name)"
            continue
        }
        Copy-Item -LiteralPath $f.FullName -Destination ([IO.Path]::Combine($dest, $f.Name))
        $existing[$key] = $f.Name
        $Origins["$GearFolder\$($f.Name)"] = $Origin
        Add-Content -LiteralPath $AddedList -Value "$GearFolder\$($f.Name)" -Encoding UTF8
        $copied++
    }
    return $copied
}

function Find-VisualStudio {
    $vswhere = [IO.Path]::Combine(${env:ProgramFiles(x86)}, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe')
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'vswhere.exe not found. Install Visual Studio 2026 (or 2022) with the "Desktop development with C++" and ".NET desktop development" workloads.'
    }
    $json = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -format json
    # Windows PowerShell 5.1 emits a JSON array as a single object; unroll it explicitly.
    $vs = @(($json -join "`n") | ConvertFrom-Json | ForEach-Object { $_ })
    if ($vs.Count -eq 0) {
        throw 'No Visual Studio with MSBuild and the C++ x64 tools found. Add the "Desktop development with C++" workload.'
    }
    $vs = $vs[0]
    $msbuild = [IO.Path]::Combine($vs.installationPath, 'MSBuild', 'Current', 'Bin', 'amd64', 'MSBuild.exe')
    if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = [IO.Path]::Combine($vs.installationPath, 'MSBuild', 'Current', 'Bin', 'MSBuild.exe') }
    [pscustomobject]@{
        Path    = $vs.installationPath
        Major   = [int]($vs.installationVersion.Split('.')[0])
        MSBuild = $msbuild
        VcVars  = [IO.Path]::Combine($vs.installationPath, 'VC', 'Auxiliary', 'Build', 'vcvars64.bat')
    }
}

function Assert-DotNet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK not found. Install the .NET 10 SDK (x64): https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $sdks = & dotnet --list-sdks
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        throw ".NET 10 SDK not found (have: $($sdks -join ', ')). Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
    }
}

function Get-GitSha {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    # In 5.1, stderr from a native command becomes a terminating error under 'Stop'.
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $sha = & git -C $Root rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return "$sha".Trim()
    } catch {
        return $null
    } finally {
        $ErrorActionPreference = $old
    }
}

# ---------------------------------------------------------------- 1. toolchain

Write-Step 'Checking toolchain'
$sha = Get-GitSha
if (-not $Version) {
    $suffix = if ($sha) { $sha } else { 'local' }
    $Version = '{0:yyyy.MM.dd}-{1}' -f (Get-Date), $suffix
}
Assert-DotNet10
$needVs = (-not $SkipCore) -or (-not $SkipNativeMachines)
$vs = $null
if ($needVs) {
    $vs = Find-VisualStudio
    Write-Host "Visual Studio $($vs.Major) at $($vs.Path)"
    if (-not $PlatformToolset -and $vs.Major -lt 18) {
        $PlatformToolset = 'v143'
        Write-Warning "Visual Studio $($vs.Major) has no v145 toolset; building C++ projects with v143."
    }
}
Write-Host "Version: $Version"
if ($env:GITHUB_ACTIONS -and $vs) {
    $tsText = if ($PlatformToolset) { $PlatformToolset } else { 'v145 (project default)' }
    Write-Host "::notice title=Toolchain::Visual Studio $($vs.Major) at $($vs.Path); C++ toolset $tsText"
}

# ---------------------------------------------------------------- 2-3. core

if (-not $SkipCore) {
    if ((Test-Path -LiteralPath ([IO.Path]::Combine($Root, '.git'))) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Step 'Updating submodules'
        Invoke-Checked git @('-C', $Root, 'submodule', 'update', '--init', '--recursive')
    }

    Write-Step "Building ReBuzz.sln ($Configuration|x64)"
    $msbuildArgs = @(
        ([IO.Path]::Combine($Root, 'ReBuzz.sln')),
        '/restore', '/m', '/nologo', '/v:minimal',
        "/p:Configuration=$Configuration", '/p:Platform=x64',
        '/p:ReBuzzCleanBuild=true'   # fixed ForcePerformRestore step (see Directory.Build.targets)
    )
    if ($PlatformToolset) { $msbuildArgs += "/p:PlatformToolset=$PlatformToolset" }
    Invoke-Checked $vs.MSBuild $msbuildArgs
}

# The app's executable (ReBuzz.csproj names its apphost NeoBuzzLive.exe; ReBuzz.Product.WindowsExe).
$AppExe = 'NeoBuzzLive.exe'
if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($OutDir, $AppExe)))) {
    throw "$AppExe not found in $OutDir. Run without -SkipCore first."
}

# ---------------------------------------------------------------- 3b. Link helper (fork, 0.8)

# ReBuzzLink.exe runs Ableton Link for the Session View in its own process: it is GPL-2.0-or-later
# (as Link is), ReBuzz talks to it over stdin/stdout. Its source and Link's licence go with it.
if (-not $SkipCore) {
    $linkSrc = [IO.Path]::Combine($Root, 'LinkHelper')
    if (Test-Path -LiteralPath ([IO.Path]::Combine($linkSrc, 'link', 'AbletonLinkConfig.cmake'))) {
        Write-Step 'Building the Link helper (ReBuzzLink.exe)'
        $linkBuild = [IO.Path]::Combine($Root, 'build', 'LinkHelper')
        Invoke-Checked cmake @('-S', $linkSrc, '-B', $linkBuild, '-A', 'x64')
        Invoke-Checked cmake @('--build', $linkBuild, '--config', $Configuration)
        $linkExe = [IO.Path]::Combine($linkBuild, $Configuration, 'ReBuzzLink.exe')
        if (-not (Test-Path -LiteralPath $linkExe)) { throw "ReBuzzLink.exe not built ($linkExe)" }
        Copy-Item -LiteralPath $linkExe -Destination $OutDir -Force
        $linkDoc = [IO.Path]::Combine($OutDir, 'LinkHelper')
        New-Dir $linkDoc
        foreach ($f in 'main.cpp', 'CMakeLists.txt', 'README.md') {
            Copy-Item -LiteralPath ([IO.Path]::Combine($linkSrc, $f)) -Destination $linkDoc -Force
        }
        foreach ($f in 'LICENSE.md', 'GNU-GPL-v2.0.md') {
            Copy-Item -LiteralPath ([IO.Path]::Combine($linkSrc, 'link', $f)) -Destination ([IO.Path]::Combine($linkDoc, "Link-$f")) -Force
        }
        Write-Host "ReBuzzLink.exe -> $OutDir"
    } else {
        Write-Warning 'LinkHelper\link (Ableton Link) is missing: run "git submodule update --init --recursive". The Session View works without Link.'
    }
}

# ---------------------------------------------------------------- 3c. NeoBuzz Live Studio (preview, fork)

# The studio (ReBuzz.Studio, Avalonia; docs/specs/ux-overhaul.md P6) published for win-x64 with PortAudio built from source
# (tools\build-portaudio.ps1), into build\Studio. Packaging puts it in the package as Studio\, beside the WPF app, and adds what
# the two share (section 6a). Framework-dependent like the WPF app. -SkipStudio leaves it out.
$StudioStage = [IO.Path]::Combine($Root, 'build', 'Studio')
if ((-not $SkipCore) -and (-not $SkipStudio)) {
    Write-Step 'Publishing NeoBuzz Live Studio (preview)'
    & ([IO.Path]::Combine($Root, 'tools', 'publish-studio-windows.ps1')) -Out $StudioStage -Configuration $Configuration
}

# Remove what a previous run added, so Gear holds only upstream files again.
New-Dir $StageRoot
if (Test-Path -LiteralPath $AddedList) {
    $removed = 0
    foreach ($rel in (Get-Content -LiteralPath $AddedList)) {
        if (-not $rel) { continue }
        $p = [IO.Path]::Combine($OutDir, 'Gear', $rel)
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; $removed++ }
    }
    Remove-Item -LiteralPath $AddedList -Force
    Write-Host "Removed $removed file(s) added to Gear by the previous run"
}

# Everything in Gear now came from upstream (RunTime\Gear or ReBuzz's own machines).
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($OutDir, 'Gear', $sub)
    if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -File | ForEach-Object { $Origins["$sub\$($_.Name)"] = 'rebuzz' }
    }
}

$manifest = Get-Content -LiteralPath ([IO.Path]::Combine($ExtRoot, 'machines.json')) -Raw | ConvertFrom-Json

# ---------------------------------------------------------------- 4a. managed machines

if (-not $SkipManagedMachines) {
    $managed = @($manifest.machines | Where-Object { $_.kind -eq 'rebuzz-managed' })
    Write-Step "Building $($managed.Count) managed external machines"

    # The machines compile against a scratch copy of the ReBuzz API assemblies. Their own
    # deploy targets then copy into this scratch folder instead of the real Gear folder.
    $refDir = [IO.Path]::Combine($StageRoot, '_ref')
    if (Test-Path -LiteralPath $refDir) { Remove-Item -LiteralPath $refDir -Recurse -Force }
    New-Dir ([IO.Path]::Combine($refDir, 'Gear', 'Generators'))
    New-Dir ([IO.Path]::Combine($refDir, 'Gear', 'Effects'))
    foreach ($asm in 'ReBuzz.dll', 'ReBuzz.Engine.dll', 'BuzzGUI.Interfaces.dll', 'BuzzGUI.Common.dll', 'NAudio.WinMM.dll') {
        $src = [IO.Path]::Combine($OutDir, $asm)
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $refDir }
        else { Write-Warning "$asm not found in the ReBuzz build; machines that reference it will fail" }
    }

    foreach ($m in $managed) {
        $projDir = [IO.Path]::Combine($ExtRoot, ($m.path -replace '/', '\'))
        $proj = [IO.Path]::Combine($projDir, $m.project)
        $stage = [IO.Path]::Combine($StageRoot, $m.id)
        Write-Host "-- $($m.id)"
        # No trailing backslash on paths: it would escape the closing quote on the command line.
        & dotnet build $proj -c $Configuration -nologo -v:minimal `
            "-p:ReBuzzDir=$refDir" "-p:ReBuzzPath=$refDir" "-p:BuzzDir=$refDir" `
            "-p:OutputPath=$stage" -p:AppendTargetFrameworkToOutputPath=false
        if ($LASTEXITCODE -ne 0) {
            Add-MachineFailure "$($m.id): dotnet build failed (exit $LASTEXITCODE)"
            continue
        }
        $dll = [IO.Path]::Combine($stage, "$($m.assembly).dll")
        if (-not (Test-Path -LiteralPath $dll)) {
            Add-MachineFailure "$($m.id): expected '$($m.assembly).dll' in $stage"
            continue
        }
        $origin = "external:$($m.id)"
        $files = @(Get-Item -LiteralPath $dll)
        $files += @(Get-ChildItem -LiteralPath $projDir -File |
            Where-Object { $_.Name -like '*.prs' -or $_.Name -like '*.prs.xml' })
        Copy-IntoGear -Files $files -GearFolder $m.gearFolder -Origin $origin | Out-Null
        [void]$BuiltRepos.Add(($m.origin -replace '^https://github.com/', ''))
    }
}

# ---------------------------------------------------------------- 4b. native machines with sources (fork, U7)

# The classic native machines whose sources are in ExternalMachines (docs/specs/mcp-server.md §12.14): built
# as x86 and x64 into build\ExternalMachines\natives\<platform>\Gear by tools\build-natives.ps1. What goes
# into Gear is each entry's "ship": "rebuilt" replaces upstream's binary of that machine with the rebuilt one
# of the same platform, or of "shipPlatform" (it renders as upstream's: tools\native-compare.py checks it in
# the package run), "upstream" keeps upstream's. A machine whose licence doesn't allow redistribution ("redistribute": false)
# is built for the comparison only, never added, and one whose licence has no text in "licenseTexts" ships upstream's binary,
# but for one exception, recorded here and nowhere else: an entry whose "licenseException" is exactly $NativeLicenceException
# (sources that carry no licence, Jeskola's: shipped rebuilt by the user's decision of 2026-11-06, for private use; NOTICE.txt
# and README-FIRST.txt say so, and that they must be re-cleared, or set back to "ship": "upstream", before any public release).
# Any other "licenseException" is a failure, and the machine ships upstream's binary.
$NativeLicenceException = 'no licence in source; shipped by user decision 2026-11-06 for private use'
$NativeLicenceException2 = 'no licence in source; shipped by user decision 2026-11-12 for private use (NOT FOR PUBLIC RELEASE AS IS)'   # Jeskola Delay and NiNjA dElaY
$NativeLicenceExceptions = @($NativeLicenceException, $NativeLicenceException2)
$ShippedByException = New-Object System.Collections.Generic.List[string]   # gear, for NOTICE.txt and README-FIRST.txt
if (-not $SkipNativeMachines) {
    $bt = $manifest.machines | Where-Object { $_.id -eq 'buzztrax-buzzmachines' } | Select-Object -First 1
    $natives = @(if ($bt) { Get-Prop $bt 'natives' })
    if ($natives.Count -gt 0) {
        Write-Step "Building $($natives.Count) native machines from source (x86, x64)"
        $nativeOut = [IO.Path]::Combine($StageRoot, 'natives')
        $buildArgs = @{ OutDir = $nativeOut; VsPath = $vs.Path }
        & ([IO.Path]::Combine($Root, 'tools', 'build-natives.ps1')) @buildArgs
        if ($LASTEXITCODE -ne 0) {
            Add-MachineFailure "natives: some machines failed to build (see the compiler output above); upstream's binaries stay for those"
        }
        foreach ($n in $natives) {
            $upFile = $n.gear + $(if ((Get-Prop $n 'upstreamBinary') -eq 'x86') { '.dll' } else { '.x64.dll' })
            # Upstream's binary back first, where a previous run (-SkipCore) replaced it.
            $upstream = [IO.Path]::Combine($Root, 'RunTime', 'Gear', $n.gearFolder, $upFile)
            $inGear = [IO.Path]::Combine($OutDir, 'Gear', $n.gearFolder, $upFile)
            if ((Test-Path -LiteralPath $upstream) -and -not (Test-Path -LiteralPath $inGear)) { Copy-Item -LiteralPath $upstream -Destination $inGear }
            if ((Get-Prop $n 'ship') -ne 'rebuilt') { continue }
            $exception = Get-Prop $n 'licenseException'
            if ($null -ne $exception) {
                if ($NativeLicenceExceptions -cnotcontains $exception) {
                    Add-MachineFailure "natives: $($n.gear): its licenseException ('$exception') isn't one of the two build.ps1 records ('$NativeLicenceException', '$NativeLicenceException2'); upstream's binary stays"
                    continue
                }
            } else {
                if ((Get-Prop $n 'redistribute') -eq $false) { continue }
                # Its licence's text ships with it (Licenses\, machines.json's "licenseTexts"), or upstream's binary stays.
                if (-not (Get-LicenseText (Get-LicenseId (Get-Prop $n 'license')))) {
                    Add-MachineFailure "natives: $($n.gear): machines.json's licenseTexts has no text of its licence ($($n.license)); upstream's binary stays"
                    continue
                }
            }
            # The platform that ships: upstream's, or "shipPlatform" (an x64 build where upstream has only x86).
            $platform = Get-Prop $n 'shipPlatform'
            if (-not $platform) { $platform = if ((Get-Prop $n 'upstreamBinary') -eq 'x86') { 'x86' } else { 'x64' } }
            $file = $n.gear + $(if ($platform -eq 'x64') { '.x64.dll' } else { '.dll' })
            $built = [IO.Path]::Combine($nativeOut, $platform, 'Gear', $n.gearFolder, $file)
            if (-not (Test-Path -LiteralPath $built)) {
                Add-MachineFailure "natives: $($n.gear) ($platform) didn't build; upstream's binary stays"
                continue
            }
            $dest = [IO.Path]::Combine($OutDir, 'Gear', $n.gearFolder)
            # Upstream's binary of the machine (either name) makes way for the rebuilt one.
            Get-ChildItem -LiteralPath $dest -File -Filter '*.dll' | Where-Object { (Get-GearKey $_.Name) -eq (Get-GearKey $file) } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
            Copy-Item -LiteralPath $built -Destination ([IO.Path]::Combine($dest, $file))
            $Origins["$($n.gearFolder)\$file"] = 'external:buzztrax'
            Add-Content -LiteralPath $AddedList -Value "$($n.gearFolder)\$file" -Encoding UTF8
            if ($null -ne $exception) {
                $ShippedByException.Add($n.gear)
                Write-Host "Gear\$($n.gearFolder)\$file rebuilt from source (in place of upstream's), its source without a licence: $exception"
            } else {
                Write-Host "Gear\$($n.gearFolder)\$file rebuilt from source (in place of upstream's)"
            }
        }
    }
}

# ---------------------------------------------------------------- 5. prebuilt binaries

if (-not $SkipBinaries) {
    Write-Step 'Merging ExternalMachines\binaries'
    # binaries\repos\<owner>__<repo>\<Generators|Effects>: fetched release assets, used only
    # when that repo's machine did not build from source in this run.
    $reposDir = [IO.Path]::Combine($ExtRoot, 'binaries', 'repos')
    if (Test-Path -LiteralPath $reposDir) {
        foreach ($repoDir in Get-ChildItem -LiteralPath $reposDir -Directory) {
            $repo = $repoDir.Name -replace '__', '/'
            if ($BuiltRepos.Contains($repo)) {
                Write-Verbose "binaries for $repo skipped: built from source"
                continue
            }
            foreach ($sub in 'Generators', 'Effects') {
                $dir = [IO.Path]::Combine($repoDir.FullName, $sub)
                if (Test-Path -LiteralPath $dir) {
                    $n = Copy-IntoGear -Files @(Get-ChildItem -LiteralPath $dir -File) -GearFolder $sub -Origin "binaries:$repo"
                    Write-Host "Added $n prebuilt file(s) from $repo to Gear\$sub"
                }
            }
        }
    }
    # binaries\Gear\<Generators|Effects>: hand-placed DLLs.
    foreach ($sub in 'Generators', 'Effects') {
        $dir = [IO.Path]::Combine($ExtRoot, 'binaries', 'Gear', $sub)
        if (Test-Path -LiteralPath $dir) {
            $n = Copy-IntoGear -Files @(Get-ChildItem -LiteralPath $dir -File) -GearFolder $sub -Origin 'binaries'
            Write-Host "Added $n hand-placed file(s) to Gear\$sub"
        }
    }
}

# ---------------------------------------------------------------- 5b. draft manifests (fork)

# Machines\Manifests\<Generators|Effects>: the profiled draft manifests of machines that don't
# generate their own (docs/specs/mcp-server.md §12.2, tools/machine-profiler.py), beside their DLLs,
# where the agent server looks for "<Gear>.machine.json". A machine's own manifest (the Neo
# machines') is never replaced. Ignored by everything but the agent server.
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($Root, 'Machines', 'Manifests', $sub)
    if (Test-Path -LiteralPath $dir) {
        $n = Copy-IntoGear -Files @(Get-ChildItem -LiteralPath $dir -File -Filter '*.machine.json') -GearFolder $sub -Origin 'manifests'
        Write-Host "Added $n draft manifest(s) to Gear\$sub"
    }
}

# Fork (docs/specs/mcp-server.md §12.11, U4): the curated machines' factory presets (Machines\Manifests\<sub>\<Gear>.prs.xml,
# compiled from <Gear>.presets.txt) beside their DLLs, where preset_load, the preset bars and the parameter window read
# "<Gear>.prs.xml". A machine that ships one of its own (Joachims Mars filter 2) keeps its presets and gets ours added.
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($Root, 'Machines', 'Manifests', $sub)
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -File -Filter '*.prs.xml')) {
        $target = [IO.Path]::Combine($OutDir, 'Gear', $sub, $f.Name)
        if (Test-Path -LiteralPath $target) {
            $have = New-Object System.Xml.XmlDocument
            $have.Load($target)
            $ours = New-Object System.Xml.XmlDocument
            $ours.Load($f.FullName)
            $keys = @{}
            foreach ($i in $have.DocumentElement.SelectNodes('Item')) { $keys[$i.GetAttribute('Key')] = $true }
            $added = 0
            foreach ($i in $ours.DocumentElement.SelectNodes('Item')) {
                if ($keys.ContainsKey($i.GetAttribute('Key'))) { continue }
                [void]$have.DocumentElement.AppendChild($have.ImportNode($i, $true))
                $added++
            }
            $have.Save($target)
            Write-Host "Added $added factory preset(s) to Gear\$sub\$($f.Name)"
        } else {
            $n = Copy-IntoGear -Files @($f) -GearFolder $sub -Origin 'factory-presets'
            Write-Host "Added $($f.Name) to Gear\$sub"
        }
    }
}

# ---------------------------------------------------------------- 6. package

Write-Step 'Packaging'
$pkgName = "NeoBuzz-Live-$Version-x64"
$pkgDir = [IO.Path]::Combine($DistDir, $pkgName)
if (Test-Path -LiteralPath $pkgDir) { Remove-Item -LiteralPath $pkgDir -Recurse -Force }
New-Dir $pkgDir

$exclude = @('*.pdb', '*.ilk', '*.exp', '*.lib', '*.iobj', '*.ipdb')
Get-ChildItem -LiteralPath $OutDir -Recurse -File | Where-Object {
    $name = $_.Name
    -not ($exclude | Where-Object { $name -like $_ })
} | Where-Object {
    # A ReBuzz.exe beside the app is an apphost left by a build from before the rename (NeoBuzzLive.exe): not shipped.
    $_.FullName.Substring($OutDir.Length).TrimStart('\') -ne 'ReBuzz.exe'
} | ForEach-Object {
    $rel = $_.FullName.Substring($OutDir.Length).TrimStart('\')
    $target = [IO.Path]::Combine($pkgDir, $rel)
    New-Dir ([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath $_.FullName -Destination $target
}

# bin64\dsplib.dll (upstream binary) imports MSVCR100.dll, the x64 VC++ 2010 runtime, which
# today's VC++ redistributable doesn't include. Without it every 64-bit native machine fails to
# load (the engine host dies with 0xC0000135). Upstream already ships the x86 one in bin32, so
# ship the x64 one in bin64 too, taken from this machine's VC++ 2010 runtime when installed.
$pkgBin64 = [IO.Path]::Combine($pkgDir, 'bin64')
$sys64 = if ([Environment]::Is64BitProcess) { 'System32' } else { 'Sysnative' }
$crt100 = [IO.Path]::Combine($env:WINDIR, $sys64, 'msvcr100.dll')
$crtBundled = Test-Path -LiteralPath ([IO.Path]::Combine($pkgBin64, 'msvcr100.dll'))
if ((Test-Path -LiteralPath $pkgBin64) -and -not (Test-Path -LiteralPath ([IO.Path]::Combine($pkgBin64, 'msvcr100.dll')))) {
    if (Test-Path -LiteralPath $crt100) {
        Copy-Item -LiteralPath $crt100 -Destination $pkgBin64
        $crtBundled = $true
        Write-Host "Bundled x64 msvcr100.dll (VC++ 2010 runtime) in bin64"
    } else {
        Write-Warning 'x64 msvcr100.dll not found on this machine (install the VC++ 2010 SP1 x64 redistributable); the package will need it installed to load 64-bit native machines'
    }
}

# What a DLL in Gear is: a managed machine (it references MachineDecl), a native Buzz machine
# (it exports GetInfo) or a library machines use. Needs System.Reflection.Metadata (PowerShell 7);
# 'unknown' where it isn't there (Windows PowerShell 5.1).
function Get-MachineKind([string]$Path) {
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $headers = $pe.PEHeaders
        if ($headers.CorHeader) {
            if (-not $pe.HasMetadata) { return 'other' }
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($handle in $md.TypeReferences) {
                if ($md.GetString($md.GetTypeReference($handle).Name) -eq 'MachineDecl') { return 'managed' }
            }
            return 'other'
        }
        # Native: its export names, read from the file (an RVA's offset by its section).
        $dir = $headers.PEHeader.ExportTableDirectory
        if ($dir.Size -le 0) { return 'other' }
        $bytes = [IO.File]::ReadAllBytes($Path)
        $sections = @($headers.SectionHeaders)
        $offsetOf = {
            param([int]$rva)
            foreach ($h in $sections) {
                $size = [Math]::Max($h.VirtualSize, $h.SizeOfRawData)
                if ($rva -ge $h.VirtualAddress -and $rva -lt $h.VirtualAddress + $size) { return $rva - $h.VirtualAddress + $h.PointerToRawData }
            }
            return -1
        }
        $at = & $offsetOf $dir.RelativeVirtualAddress
        if ($at -lt 0) { return 'other' }
        $count = [BitConverter]::ToInt32($bytes, $at + 24)
        $table = & $offsetOf ([BitConverter]::ToInt32($bytes, $at + 32))
        if ($table -lt 0) { return 'other' }
        for ($i = 0; $i -lt [Math]::Min($count, 4096); $i++) {
            $name = & $offsetOf ([BitConverter]::ToInt32($bytes, $table + 4 * $i))
            if ($name -lt 0) { continue }
            $end = [Array]::IndexOf($bytes, [byte]0, $name)
            if ($end -gt $name -and [Text.Encoding]::ASCII.GetString($bytes, $name, $end - $name) -eq 'GetInfo') { return 'native' }
        }
        return 'other'
    } catch {
        return 'unknown'
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

$canReadDlls = $true
try { Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction Stop } catch { $canReadDlls = $false }
if ($canReadDlls -and -not ('System.Reflection.PortableExecutable.PEReader' -as [type])) { $canReadDlls = $false }

# Inventory of machine DLLs and where each came from (and, where it can be read, what each is).
# "Foo" and "Foo.x64" count as one machine.
$inventory = New-Object System.Collections.Generic.List[string]
$inventory.Add("Machines in this package ($pkgName)")
$inventory.Add('Origin: rebuzz = upstream ReBuzz (RunTime\Gear or its own machines), external:<id> = built from ExternalMachines, binaries[:repo] = prebuilt from ExternalMachines\binaries')
if ($canReadDlls) { $inventory.Add('Kind: managed or native machine, or a library machines use') }
$machineNames = New-Object 'System.Collections.Generic.HashSet[string]'
$summary = @()
$kinds = @{}
$byOrigin = @{}
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($pkgDir, 'Gear', $sub)
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $dlls = @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll' | Sort-Object Name)
    $rows = New-Object System.Collections.Generic.List[string]
    $machines = 0
    foreach ($d in $dlls) {
        $origin = $Origins["$sub\$($d.Name)"]
        if (-not $origin) { $origin = 'unknown' }
        if (-not $canReadDlls) {
            $rows.Add(('  {0,-48} {1}' -f $d.Name, $origin))
            continue
        }
        $kind = Get-MachineKind $d.FullName
        $kinds[$kind] = 1 + [int]$kinds[$kind]
        $label = switch ($kind) { 'managed' { 'managed machine' } 'native' { 'native machine' } 'other' { 'library' } default { 'unknown' } }
        $rows.Add(('  {0,-48} {1,-28} {2}' -f $d.Name, $origin, $label))
        if (($kind -eq 'managed' -or $kind -eq 'native') -and $machineNames.Add("$sub\" + ($d.BaseName -replace '\.x64$', ''))) {
            $machines++
            $where = if ($origin -like 'external:*') { 'built from ExternalMachines' } elseif ($origin -like 'binaries*') { 'prebuilt binaries' } elseif ($origin -eq 'unknown') { 'unknown' } else { 'upstream and own' }
            $byOrigin[$where] = 1 + [int]$byOrigin[$where]
        }
    }
    $inventory.Add('')
    $inventory.Add($(if ($canReadDlls) { "[$sub] $machines machines, $($dlls.Count) DLLs" } else { "[$sub] $($dlls.Count) DLLs" }))
    $inventory.AddRange($rows)
    $summary += "$machines $($sub.ToLowerInvariant())"
}
$inventory | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'GEAR-INVENTORY.txt')) -Encoding UTF8

# On CI, the inventory as annotations (the package itself is an artifact to download): a summary,
# then the file in pieces under GitHub's 4 kB a message (a workflow command is one line).
if ($env:GITHUB_ACTIONS -eq 'true') {
    $vst = [IO.Path]::Combine($pkgDir, 'Gear', 'vst')
    $vstCount = if (Test-Path -LiteralPath $vst) { @(Get-ChildItem -LiteralPath $vst -Recurse -File -Filter '*.dll').Count } else { 0 }
    $originText = ($byOrigin.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', '
    $kindText = ($kinds.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', '
    $head = if ($canReadDlls) { "$($machineNames.Count) machines in Gear: $($summary -join ', ') ($originText). DLLs: $kindText." } else { 'Machine count unavailable (no System.Reflection.Metadata).' }
    Write-Host "::notice title=Package::$head Gear\vst: $vstCount DLLs. Machine build failures: $($Failures.Count)"
    $pieces = New-Object System.Collections.Generic.List[string]
    $piece = ''
    foreach ($line in $inventory) {
        if ($piece.Length + $line.Length + 1 -gt 3500) { $pieces.Add($piece); $piece = '' }
        $piece += $line + "`n"
    }
    if ($piece.Length -gt 0) { $pieces.Add($piece) }
    for ($i = 0; $i -lt $pieces.Count; $i++) {
        $text = $pieces[$i].TrimEnd("`n").Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::notice title=GEAR-INVENTORY.txt ($($i + 1)/$($pieces.Count))::$text"
    }
}

# ---------------------------------------------------------------- 6a. NeoBuzz Live Studio (preview)

# Studio\ in the package: NeoBuzzLiveStudio.exe with its own binaries (they can't share the app's folder: the studio runs on the
# portable builds of BuzzGUI.Interfaces and BuzzGUI.Common, the WPF app on the Windows builds, under the same names) and its own
# Gear: the Neo machines and the pattern editor built with their Avalonia editors, and the Pedal machines. What the two apps share
# is copied from the app's Gear and folders, not rebuilt: the native machine hosts (bin32, bin64), the native machines (x86 and
# x64 DLLs, which run out of process in those hosts) and their support files and manifests, and the native libraries. Managed
# machines built for WPF stay out: the studio can't load them, so a song with one shows it as not installed.
function Test-ManagedDll([string]$Path) {
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        return [bool]$pe.PEHeaders.CorHeader
    } catch {
        return $null
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

$studioDir = [IO.Path]::Combine($pkgDir, 'Studio')
$studioSummary = 'not built (-SkipStudio)'
if (Test-Path -LiteralPath ([IO.Path]::Combine($StudioStage, 'NeoBuzzLiveStudio.exe'))) {
    Write-Step 'Adding NeoBuzz Live Studio (preview) to the package'
    New-Dir $studioDir
    Copy-Item -Path ([IO.Path]::Combine($StudioStage, '*')) -Destination $studioDir -Recurse -Force
    Get-ChildItem -LiteralPath $studioDir -Recurse -File | Where-Object { $name = $_.Name; @($exclude | Where-Object { $name -like $_ }).Count -gt 0 } | Remove-Item -Force
    foreach ($bin in 'bin32', 'bin64') {
        $from = [IO.Path]::Combine($pkgDir, $bin)
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination ([IO.Path]::Combine($studioDir, $bin)) -Recurse -Force }
    }
    foreach ($lib in (Get-ChildItem -LiteralPath $pkgDir -File -Filter 'r8bsrc*.dll')) {
        Copy-Item -LiteralPath $lib.FullName -Destination $studioDir -Force
    }
    $rootGear = [IO.Path]::Combine($pkgDir, 'Gear')
    $studioGear = [IO.Path]::Combine($studioDir, 'Gear')
    $sharedNative = 0; $sharedOther = 0; $leftOut = 0
    foreach ($f in Get-ChildItem -LiteralPath $rootGear -Recurse -File) {
        $rel = $f.FullName.Substring($rootGear.Length).TrimStart('\')
        $dest = [IO.Path]::Combine($studioGear, $rel)
        if (Test-Path -LiteralPath $dest) { continue }                  # the studio's own build of it
        if ($f.Extension -eq '.dll') {
            $managed = if ($canReadDlls) { Test-ManagedDll $f.FullName } else { $null }
            if ($managed -ne $false) { $leftOut++; continue }           # a managed machine or library (WPF), or not readable
            $sharedNative++
        } else {
            $sharedOther++
        }
        New-Dir ([IO.Path]::GetDirectoryName($dest))
        Copy-Item -LiteralPath $f.FullName -Destination $dest
    }
    $studioBytes = (Get-ChildItem -LiteralPath $studioDir -Recurse -File | Measure-Object Length -Sum).Sum
    $studioSummary = "Studio\ $([math]::Round($studioBytes / 1MB, 1)) MB: $sharedNative native DLLs and $sharedOther other files shared from Gear, $leftOut managed (WPF) DLLs left out"
    Write-Host $studioSummary
} elseif (-not $SkipStudio) {
    Write-Warning "NeoBuzz Live Studio (preview) not found in $StudioStage: the package has no Studio\ folder"
}

$toolsetText = if ($PlatformToolset) { $PlatformToolset } else { 'project default (v145)' }
$shaText = if ($sha) { $sha } else { 'unknown' }
$info = @(
    "NeoBuzz Live build $Version (a fork of ReBuzz)",
    "Commit:      $shaText",
    "Built:       $((Get-Date).ToString('u'))",
    "Config:      $Configuration|x64",
    "C++ toolset: $toolsetText",
    "VC++ 2010 x64 runtime (bin64\msvcr100.dll) bundled: $(if ($crtBundled) { 'yes' } else { 'NO - install the VC++ 2010 SP1 x64 redistributable' })",
    "NeoBuzz Live Studio (preview): $studioSummary",
    "Machine build failures: $($Failures.Count)"
)
$info += @($Failures | ForEach-Object { "  - $_" })
$info | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'BUILD-INFO.txt')) -Encoding UTF8

# ---------------------------------------------------------------- 6b. licences (fork)

# Licenses\NOTICE.txt names each machine DLL in Gear built from ExternalMachines (or prebuilt from its repos' releases),
# from what machines.json says of it: its licence, where its source is in this repository and upstream, and what was
# changed. The text of each of their licences that machines.json's "licenseTexts" has ships beside it (Licenses\<id>.txt),
# so a machine added there later gets its licence's text too. Then the components that aren't machines whose licence
# texts are in the repository (ReBuzz's own MIT licence, Serilog's, r8brain's and Concentus's).
$licDir = [IO.Path]::Combine($pkgDir, 'Licenses')
New-Dir $licDir
$bt = $manifest.machines | Where-Object { $_.id -eq 'buzztrax-buzzmachines' } | Select-Object -First 1
$groups = [ordered]@{}           # machine entry id -> files of it in Gear
$shippedTexts = [ordered]@{}     # licence id -> text, for those that ship
$byLicence = [ordered]@{}        # licence -> machine names, for README-FIRST
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($pkgDir, 'Gear', $sub)
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($d in @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll' | Sort-Object Name)) {
        $origin = $Origins["$sub\$($d.Name)"]
        $entry = $null; $native = $null; $how = 'built from source'
        if ($origin -eq 'external:buzztrax' -and $bt) {
            $entry = $bt
            $native = @(Get-Prop $bt 'natives') | Where-Object { (Get-GearKey ($_.gear + '.dll')) -eq (Get-GearKey $d.Name) } | Select-Object -First 1
        } elseif ($origin -like 'external:*') {
            $entry = $manifest.machines | Where-Object { $_.id -eq $origin.Substring(9) } | Select-Object -First 1
        } elseif ($origin -like 'binaries:*') {
            $entry = $manifest.machines | Where-Object { $_.origin -eq ('https://github.com/' + $origin.Substring(9)) } | Select-Object -First 1
            $how = "prebuilt, from the repository's latest release"
        }
        if (-not $entry) { continue }
        $licence = if ($native) { Get-Prop $native 'license' } else { Get-Prop $entry 'license' }
        $text = Get-LicenseText (Get-LicenseId $licence)
        if ($text) { $shippedTexts[$text.Id] = $text }
        if (-not $groups.Contains($entry.id)) { $groups[$entry.id] = New-Object System.Collections.Generic.List[object] }
        $groups[$entry.id].Add([pscustomobject]@{ File = "Gear\$sub\$($d.Name)"; Licence = $licence; Text = $text; Native = $native; How = $how })
        $key = if ($text) { "$($text.Id) (Licenses\$($text.Id).txt)" } else { "$licence" }
        if (-not $byLicence.Contains($key)) { $byLicence[$key] = New-Object System.Collections.Generic.List[string] }
        $byLicence[$key].Add(($d.BaseName -replace '\.x64$', ''))
    }
}
$notice = New-Object System.Collections.Generic.List[string]
$notice.Add("Licences in this package ($pkgName)")
$notice.Add('')
$notice.Add("Machines built from the sources in this fork's ExternalMachines folder (neo-buzz repository, commit $shaText),")
$notice.Add('each with its licence, its source and upstream, and what this fork changed. A licence''s text is in this folder.')
if ($ShippedByException.Count -gt 0) {
    $notice.Add('')
    $notice.Add("NOT FOR PUBLIC RELEASE AS IS: $($ShippedByException -join ', ') are built from sources that carry no licence (no licence file, no")
    $notice.Add("header), and ship rebuilt only by an exception: $($NativeLicenceExceptions -join '; or '). Before any public release they must be")
    $notice.Add('re-cleared with their author, or reverted to upstream''s binaries (machines.json: "ship": "upstream").')
}
foreach ($id in $groups.Keys) {
    $entry = $manifest.machines | Where-Object { $_.id -eq $id } | Select-Object -First 1
    $notice.Add('')
    $notice.Add("ExternalMachines\$($entry.path -replace '/', '\')")
    $notice.Add("  upstream: $($entry.origin) (commit $($entry.commit))")
    $patches = @(@(Get-Prop $entry 'patches') | Where-Object { $_ })
    if ($patches.Count -gt 0) { $notice.Add("  changed here: $($patches -join '; ')") }
    foreach ($f in $groups[$id]) {
        $notice.Add("  $($f.File)")
        $licText = if ($f.Text) { "$($f.Licence); text: Licenses\$($f.Text.Id).txt" } elseif ("$($f.Licence)" -like 'None*') { "$($f.Licence): no licence text to ship" } else { "$($f.Licence)" }
        $notice.Add("    licence: $licText")
        if ($f.Native -and $NativeLicenceExceptions -ccontains (Get-Prop $f.Native 'licenseException')) {
            $notice.Add("    exception: $(Get-Prop $f.Native 'licenseException'). Its source carries no licence: re-clear it with its author, or ship upstream's binary again (machines.json: ""ship"": ""upstream""), before any public release")
        }
        if ($f.Native) {
            $notice.Add("    source:  ExternalMachines\$($entry.path -replace '/', '\')\$($f.Native.dir -replace '/', '\') ($($f.Native.sources -join ', ')); upstream's $($f.Native.dir)")
            if (Get-Prop $f.Native 'fix') { $notice.Add("    fixed:   $($f.Native.fix)") }
        } else {
            $notice.Add("    $($f.How)")
        }
    }
}
foreach ($t in $shippedTexts.Values) {
    Copy-Item -LiteralPath $t.Path -Destination ([IO.Path]::Combine($licDir, "$($t.Id).txt")) -Force
}
# Components that aren't machines, built from source here or bundled, whose licence text is in the repository.
$components = @(
    [pscustomobject]@{ Name = 'NeoBuzz Live and ReBuzz (wasteddesign/ReBuzz), which it is based on'; Licence = 'MIT'; Text = 'LICENSE'; As = 'ReBuzz-LICENSE.txt'; File = $AppExe },
    [pscustomobject]@{ Name = 'Serilog (serilog.dll, from 3rdParty\serilog\serilog)'; Licence = 'Apache-2.0'; Text = '3rdParty\serilog\serilog\LICENSE'; As = 'Serilog-LICENSE.txt'; File = 'serilog.dll' },
    [pscustomobject]@{ Name = 'Serilog.Sinks.RichTextBox.Wpf (from 3rdParty\serilog\serilog-sinks-richtextbox)'; Licence = 'Apache-2.0'; Text = '3rdParty\serilog\serilog-sinks-richtextbox\LICENSE'; As = 'Serilog.Sinks.RichTextBox.Wpf-LICENSE.txt'; File = 'Serilog.Sinks.RichTextBox.Wpf.dll' },
    [pscustomobject]@{ Name = 'r8brain-free-src (r8bsrc64.dll, from 3rdParty\r8b\r8brain-free-src)'; Licence = 'MIT'; Text = '3rdParty\r8b\r8brain-free-src\LICENSE'; As = 'r8brain-free-src-LICENSE.txt'; File = 'r8bsrc64.dll' },
    [pscustomobject]@{ Name = 'PortAudio (Studio\portaudio.dll, built from source with CMake: MME, DirectSound, WASAPI and WDM-KS, no ASIO; see Studio\PortAudio-BUILD.txt)'; Licence = 'MIT'; Text = ([IO.Path]::Combine($StudioStage, 'PortAudio-LICENSE.txt')); As = 'PortAudio-LICENSE.txt'; File = 'Studio\portaudio.dll' },
    [pscustomobject]@{ Name = 'Concentus 2.2.2 (Concentus.dll, the Opus preview; NuGet, from 3rdParty\concentus)'; Licence = 'BSD-3-Clause'; Text = '3rdParty\concentus\LICENSE'; As = 'Concentus-LICENSE.txt'; File = 'Concentus.dll' }
)
$notice.Add('')
$notice.Add('Other components and their licences')
foreach ($c in $components) {
    $src = [IO.Path]::Combine($Root, $c.Text)
    if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, $c.File)))) { continue }
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination ([IO.Path]::Combine($licDir, $c.As)) -Force
        $notice.Add("  $($c.Name): $($c.Licence); text: Licenses\$($c.As)")
    } else {
        $notice.Add("  $($c.Name): $($c.Licence) (its text, $($c.Text), wasn't in this checkout)")
        Write-Warning "Licence text $($c.Text) not found (submodules not initialised?): $($c.Name) ships without it"
    }
}
if (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, 'Studio', 'portaudio.dll'))) {
    $notice.Add('  NeoBuzz Live Studio (preview), Studio\: Avalonia, SkiaSharp and HarfBuzzSharp (MIT), the Inter typeface (SIL OFL 1.1), from their NuGet packages')
}
if (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, 'LinkHelper', 'Link-LICENSE.md'))) {
    $notice.Add('  ReBuzzLink.exe (Ableton Link): GPL-2.0-or-later; text and source: LinkHelper\ (Link-LICENSE.md, Link-GNU-GPL-v2.0.md, main.cpp)')
}
$notice | Set-Content -LiteralPath ([IO.Path]::Combine($licDir, 'NOTICE.txt')) -Encoding UTF8
$licenceReadme = New-Object System.Collections.Generic.List[string]
if ($byLicence.Count -gt 0) {
    $licenceReadme.Add('Machines built from source in this package, by licence (Licenses\NOTICE.txt: where each one''s source is):')
    foreach ($k in $byLicence.Keys) { $licenceReadme.Add("  $k`: $($byLicence[$k] -join ', ')") }
}
if ($ShippedByException.Count -gt 0) {
    $licenceReadme.Add("Built from sources that carry no licence, for private use only: $($ShippedByException -join ', ') ($($NativeLicenceExceptions -join '; ')).")
    $licenceReadme.Add('  They must be re-cleared with their author, or reverted to upstream''s binaries, before any public release (Licenses\NOTICE.txt).')
}
$licenceReadme.Add('Licenses\ also has the MIT licence NeoBuzz Live keeps from ReBuzz (ReBuzz-LICENSE.txt) and the licence texts of the libraries built from source with it.')
Write-Host "Licenses\: $((@(Get-ChildItem -LiteralPath $licDir -File | ForEach-Object Name)) -join ', ')"

$readme = @'
NeoBuzz Live - portable build
=============================

NeoBuzz Live is a fork of ReBuzz, by WDE and the ReBuzz contributors (MIT licence):
https://github.com/wasteddesign/ReBuzz

Start: unzip anywhere and run NeoBuzzLive.exe. The first start scans all machines in Gear\, which takes
a while. Songs (.bmx, .bmxml), settings and machines from ReBuzz carry over as they are: the settings stay
where ReBuzz keeps them (%APPDATA%\ReBuzz, %LOCALAPPDATA%\ReBuzz, Documents\ReBuzz and the registry's
Software\ReBuzz).

NeoBuzz Live Studio (preview): Studio\NeoBuzzLiveStudio.exe is the new interface (the studio: browser,
device view, Preferences, Session View, History), in a folder of its own beside the app above. It is a
preview of the studio that is to replace the app above. It shares the machines in Gear\ (native Buzz machines,
x86 and x64, run in its own copy of bin32 and bin64) and the songs, but not the settings: it keeps its own in
%APPDATA%\NeoBuzz Live. What is not in the preview yet:
  - Native machines show no window of their own: use their parameter window and the device view.
  - Managed machines built for the app above (WPF) aren't loaded; a song with one shows it as not
    installed. The Neo machines, the Pattern Editor and the Pedal machines are there with their new editors.
  - Audio: Preferences > Audio lists the installed ASIO drivers (the same path as the app above, through NAudio;
    the default when one is installed), then the WASAPI, WDM-KS, DirectSound and MME devices (PortAudio).
  - MIDI: WinMM inputs, outputs and clock; no Windows MIDI Services endpoint, no virtual ports.
  - No installer, no file associations, no vertical sequence editor, wavetable, Latency and Signal Analysis
    windows, CPU monitor or Hard Disk Recorder yet (docs/specs/ux-overhaul.md section 9).
  Agents: Preferences > Agent, then the line shown there (the studio's server listens on a port; the app
  above's "--agent" runs headless on stdio).

AI agents: with the window open, Settings > Agent > Allow agents, then Ctrl+Shift+A shows the line to
connect Claude Code (the server is neobuzz-live). Headless, on stdio:
  claude mcp add neobuzz-live -- "<this folder>\NeoBuzzLive.exe" --agent

Needs (same as upstream ReBuzz):
  1. .NET 10 Desktop Runtime x64   https://dotnet.microsoft.com/download/dotnet/10.0
  2. Microsoft Visual C++ Redistributable, x86 AND x64 (latest)
                                    https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
     bin64\dsplib.dll also needs the x64 VC++ 2010 runtime (msvcr100.dll). It is bundled in
     bin64 unless BUILD-INFO.txt says otherwise.
  3. Windows MIDI Services SDK Runtime and Tools   https://microsoft.github.io/MIDI/

GEAR-INVENTORY.txt lists every machine and where it came from. BUILD-INFO.txt lists any machine
that failed to build. Machine licenses vary; see ExternalMachines\README.md in the source repo.
'@
@($readme -split "`r?`n") + @($licenceReadme) | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'README-FIRST.txt')) -Encoding UTF8
$licenceReadme | ForEach-Object { Write-Host "README-FIRST.txt: $_" }
if ($ShippedByException.Count -gt 0 -and $env:GITHUB_ACTIONS -eq 'true') {
    Write-Host "::notice title=Built from sources without a licence::$($ShippedByException -join ', ') ship rebuilt by the exceptions build.ps1 records ($($NativeLicenceExceptions -join '; ')): re-clear them, or revert them to upstream's binaries, before any public release"
}

if (-not $NoZip) {
    $zip = [IO.Path]::Combine($DistDir, "$pkgName.zip")
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    # CompressionLevel lives in System.IO.Compression; ZipFile in ...FileSystem. 5.1 needs both.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($pkgDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "Package: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
    # Fork (licences): the licence files as the zip has them, in the log, the step summary and an annotation.
    $licEntries = New-Object System.Collections.Generic.List[string]
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $archive.Entries) {
            if ($e.FullName -match '[\\/]Licenses[\\/].|LICENSE[^\\/]*$|COPYING[^\\/]*$|GPL[^\\/]*\.(md|txt)$') {
                $licEntries.Add(('{0} ({1} bytes)' -f ($e.FullName -replace '\\', '/'), $e.Length))
            }
        }
    } finally {
        $archive.Dispose()
    }
    Write-Host "Licence files in the zip ($($licEntries.Count)):"
    $licEntries | ForEach-Object { Write-Host "  $_" }
    $noticeFile = [IO.Path]::Combine($pkgDir, 'Licenses', 'NOTICE.txt')
    Write-Host 'Licenses\NOTICE.txt:'
    Get-Content -LiteralPath $noticeFile | ForEach-Object { Write-Host "  $_" }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::notice title=Licence files in the zip ($($licEntries.Count))::$(($licEntries -join '; ').Replace('%', '%25'))"
        if ($env:GITHUB_STEP_SUMMARY) {
            $md = @('### Licence files in the zip', '') + @($licEntries | ForEach-Object { "- ``$_``" }) +
                  @('', '<details><summary>Licenses/NOTICE.txt</summary>', '', '```') + @(Get-Content -LiteralPath $noticeFile) + @('```', '</details>', '')
            $md | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8
        }
    }
} else {
    Write-Host "Package folder: $pkgDir" -ForegroundColor Green
}

if ($Failures.Count -gt 0) {
    Write-Warning "$($Failures.Count) machine build problem(s); affected machines are missing from the package:"
    $Failures | ForEach-Object { Write-Warning "  $_" }
    if ($env:GITHUB_ACTIONS) {
        $Failures | ForEach-Object { Write-Host "::warning title=Machine build::$_" }
    }
}

# Machine failures are reported, not fatal (see -StrictMachines). Don't let the last native
# tool's exit code (nmake, dotnet) leak out as the script's result.
exit 0
