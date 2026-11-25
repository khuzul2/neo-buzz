<#
.SYNOPSIS
    Builds NeoBuzz Live for Windows x64 and packs a ready-to-run zip: dist\NeoBuzz-Live-<version>-x64.zip, whose
    NeoBuzzLive.exe is the studio (ReBuzz.Studio, Avalonia).

.DESCRIPTION
    1. Finds the .NET 10 SDK (and Visual Studio's MSBuild and C++ tools, for the native helpers).
    2. Initialises git submodules.
    3. Publishes the studio for win-x64 with tools\publish-studio-windows.ps1 into build\Studio: the app, its Gear (the Neo
       machines with their Avalonia editors and the Modern Pattern Editor), libsndfile and PortAudio built from source.
    4. Builds the native helpers beside it: the Ableton Link helper (ReBuzzLink.exe), the plug-in host behind Neo Plug
       (Gear\NeoPlug: neoplug.dll, neoplug-scan.exe, neoplug-server.exe), the Pro warp mode (neostretch.dll) and the
       r8brain resampler (r8bsrc64.dll).
    5. Lays out the package: NeoBuzzLive.exe, Gear\ and LinkHelper\ at the root, the program's own files in App\
       (the apphost is patched to start App\ReBuzz.Studio.dll), and writes GEAR-INVENTORY.txt, BUILD-INFO.txt,
       README-FIRST.txt and Licenses\ (NOTICE.txt and the licence texts), then zips it to dist\.

    Clean break (docs/specs/clean-break.md, CB1): there is no Classic app, no native machine host and no old machine in the
    package any more.

    Run from a normal PowerShell prompt on Windows (Windows PowerShell 5.1 or pwsh 7).

.PARAMETER Version
    Version label for the package. Default: yyyy.MM.dd-<git short sha>.

.PARAMETER PlatformToolset
    Override the C++ toolset of the r8brain project. It asks for v145 (Visual Studio 2026). When an older Visual
    Studio is found and this is not set, v143 is used with a warning.

.PARAMETER SkipCore
    Reuse an existing build\Studio (package only).

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -NoZip
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$PlatformToolset,
    [switch]$SkipCore,
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
$StudioStage = [IO.Path]::Combine($Root, 'build', 'Studio')
$AppExe = 'NeoBuzzLive.exe'
$Failures = New-Object System.Collections.Generic.List[string]

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

function New-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Find-VisualStudio {
    $vswhere = [IO.Path]::Combine(${env:ProgramFiles(x86)}, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe')
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $json = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -format json
    # Windows PowerShell 5.1 emits a JSON array as a single object; unroll it explicitly.
    $vs = @(($json -join "`n") | ConvertFrom-Json | ForEach-Object { $_ })
    if ($vs.Count -eq 0) { return $null }
    $vs = $vs[0]
    $msbuild = [IO.Path]::Combine($vs.installationPath, 'MSBuild', 'Current', 'Bin', 'amd64', 'MSBuild.exe')
    if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = [IO.Path]::Combine($vs.installationPath, 'MSBuild', 'Current', 'Bin', 'MSBuild.exe') }
    [pscustomobject]@{
        Path    = $vs.installationPath
        Major   = [int]($vs.installationVersion.Split('.')[0])
        MSBuild = $msbuild
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

function Add-Failure([string]$Text) {
    $Failures.Add($Text)
    Write-Warning $Text
}

# ---------------------------------------------------------------- 1. toolchain

Write-Step 'Checking toolchain'
$sha = Get-GitSha
if (-not $Version) {
    $suffix = if ($sha) { $sha } else { 'local' }
    $Version = '{0:yyyy.MM.dd}-{1}' -f (Get-Date), $suffix
}
Assert-DotNet10
$vs = Find-VisualStudio
if ($vs) {
    Write-Host "Visual Studio $($vs.Major) at $($vs.Path)"
    if (-not $PlatformToolset -and $vs.Major -lt 18) {
        $PlatformToolset = 'v143'
        Write-Warning "Visual Studio $($vs.Major) has no v145 toolset; building C++ projects with v143."
    }
} else {
    Write-Warning 'No Visual Studio with MSBuild and the C++ x64 tools: the r8brain resampler is left out (resampling falls back to the engine''s own).'
}
$haveCmake = [bool](Get-Command cmake -ErrorAction SilentlyContinue)
if (-not $haveCmake) { Write-Warning 'cmake is missing: the Link helper, the plug-in host and the Pro warp mode are left out.' }
Write-Host "Version: $Version"

# ---------------------------------------------------------------- 2-4. build

if (-not $SkipCore) {
    if ((Test-Path -LiteralPath ([IO.Path]::Combine($Root, '.git'))) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Step 'Updating submodules'
        Invoke-Checked git @('-C', $Root, 'submodule', 'update', '--init', '--recursive')
    }

    # 3. The studio (docs/specs/ux-overhaul.md P6), framework-dependent, with its Gear and PortAudio built from source.
    Write-Step 'Publishing NeoBuzz Live (the studio, win-x64)'
    & ([IO.Path]::Combine($Root, 'tools', 'publish-studio-windows.ps1')) -Out $StudioStage -Configuration $Configuration

    # 4a. ReBuzzLink.exe runs Ableton Link for the Session View in its own process: it is GPL-2.0-or-later (as Link is), the
    # app talks to it over stdin/stdout. Its source and Link's licence go with it (LinkHelper\).
    $linkSrc = [IO.Path]::Combine($Root, 'LinkHelper')
    if ($haveCmake -and (Test-Path -LiteralPath ([IO.Path]::Combine($linkSrc, 'link', 'AbletonLinkConfig.cmake')))) {
        Write-Step 'Building the Link helper (ReBuzzLink.exe)'
        $linkBuild = [IO.Path]::Combine($Root, 'build', 'LinkHelper')
        Invoke-Checked cmake @('-S', $linkSrc, '-B', $linkBuild, '-A', 'x64')
        Invoke-Checked cmake @('--build', $linkBuild, '--config', $Configuration)
        $linkExe = [IO.Path]::Combine($linkBuild, $Configuration, 'ReBuzzLink.exe')
        if (-not (Test-Path -LiteralPath $linkExe)) { throw "ReBuzzLink.exe not built ($linkExe)" }
        Copy-Item -LiteralPath $linkExe -Destination $StudioStage -Force
        $linkDoc = [IO.Path]::Combine($StudioStage, 'LinkHelper')
        New-Dir $linkDoc
        foreach ($f in 'main.cpp', 'CMakeLists.txt', 'README.md') {
            Copy-Item -LiteralPath ([IO.Path]::Combine($linkSrc, $f)) -Destination $linkDoc -Force
        }
        foreach ($f in 'LICENSE.md', 'GNU-GPL-v2.0.md') {
            Copy-Item -LiteralPath ([IO.Path]::Combine($linkSrc, 'link', $f)) -Destination ([IO.Path]::Combine($linkDoc, "Link-$f")) -Force
        }
        Write-Host "ReBuzzLink.exe -> $StudioStage"
    } else {
        Add-Failure 'The Link helper was not built (LinkHelper\link missing: run "git submodule update --init --recursive", or no cmake). The Session View works without Link.'
    }

    # 4b. Neo Plug and Neo Plug FX host CLAP and VST3 plug-ins through neoplug (PluginHost\, C++ over the VST3 SDK, MIT, and the
    # CLAP headers, MIT): the native library, its scanner and its server go to Gear\NeoPlug (the machines themselves are in the
    # studio's Gear). The SDKs are cloned into build\PluginHost-deps unless NEOPLUG_VST3_SDK and NEOPLUG_CLAP name a copy.
    $npSrc = [IO.Path]::Combine($Root, 'PluginHost')
    if ($haveCmake -and (Test-Path -LiteralPath $npSrc)) {
        Write-Step 'Building the plug-in host (neoplug.dll, neoplug-scan.exe, neoplug-server.exe)'
        $npDeps = [IO.Path]::Combine($Root, 'build', 'PluginHost-deps')
        $npBuild = [IO.Path]::Combine($Root, 'build', 'PluginHost')
        $vstSdk = $env:NEOPLUG_VST3_SDK
        $clapSdk = $env:NEOPLUG_CLAP
        if (-not $vstSdk) {
            $vstSdk = [IO.Path]::Combine($npDeps, 'vst3sdk')
            if (-not (Test-Path -LiteralPath $vstSdk)) {
                Invoke-Checked git @('clone', '-q', '--depth', '1', '--branch', 'v3.8.1_build_84', 'https://github.com/steinbergmedia/vst3sdk', $vstSdk)
                Invoke-Checked git @('-C', $vstSdk, '-c', 'advice.detachedHead=false', 'submodule', 'update', '-q', '--init', '--depth', '1', 'base', 'pluginterfaces', 'public.sdk', 'cmake')
            }
        }
        if (-not $clapSdk) {
            $clapSdk = [IO.Path]::Combine($npDeps, 'clap')
            if (-not (Test-Path -LiteralPath $clapSdk)) {
                Invoke-Checked git @('-c', 'advice.detachedHead=false', 'clone', '-q', '--depth', '1', '--branch', '1.2.10', 'https://github.com/free-audio/clap', $clapSdk)
            }
        }
        Invoke-Checked cmake @('-S', $npSrc, '-B', $npBuild, '-A', 'x64', '-DNEOPLUG_FIXTURES=OFF', "-DNEOPLUG_VST3_SDK=$vstSdk", "-DNEOPLUG_CLAP=$clapSdk")
        Invoke-Checked cmake @('--build', $npBuild, '--config', $Configuration, '--target', 'neoplug', 'neoplug-scan', 'neoplug-server')
        $npGear = [IO.Path]::Combine($StudioStage, 'Gear', 'NeoPlug')
        New-Dir $npGear
        foreach ($f in 'neoplug.dll', 'neoplug-scan.exe', 'neoplug-server.exe') {
            Copy-Item -LiteralPath ([IO.Path]::Combine($npBuild, 'bin', $f)) -Destination $npGear -Force
        }
        Write-Host "Gear\NeoPlug -> $StudioStage"
    } else {
        Add-Failure 'The plug-in host was not built (PluginHost or cmake missing): Neo Plug finds no plug-ins in this package.'
    }

    # 4c. neostretch.dll is the Pro warp mode of Neo Audio: a small C ABI over Signalsmith Stretch (MIT; the headers it needs
    # are in NeoStretch\third_party). Without it Pro clips play as Complex.
    $nsSrc = [IO.Path]::Combine($Root, 'NeoStretch')
    if ($haveCmake -and (Test-Path -LiteralPath $nsSrc)) {
        Write-Step 'Building the Pro warp mode (neostretch.dll)'
        $nsBuild = [IO.Path]::Combine($Root, 'build', 'NeoStretch')
        Invoke-Checked cmake @('-S', $nsSrc, '-B', $nsBuild, '-A', 'x64')
        Invoke-Checked cmake @('--build', $nsBuild, '--config', $Configuration)
        # The CMake file puts the library in the build folder itself, whatever the configuration; a generator that adds one is covered.
        $nsDll = @([IO.Path]::Combine($nsBuild, 'neostretch.dll'), [IO.Path]::Combine($nsBuild, $Configuration, 'neostretch.dll')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $nsDll) { throw "neostretch.dll not built under $nsBuild" }
        Copy-Item -LiteralPath $nsDll -Destination $StudioStage -Force
        Write-Host "neostretch.dll -> $StudioStage"
    } else {
        Add-Failure 'neostretch.dll was not built (NeoStretch or cmake missing): Pro clips play as Complex in this package.'
    }

    # 4d. The r8brain resampler (3rdParty\r8b, MIT) the engine calls through P/Invoke (ReBuzz\Audio\r8brain).
    $r8bProj = [IO.Path]::Combine($Root, '3rdParty', 'r8b', 'r8b.vcxproj')
    if ($vs -and (Test-Path -LiteralPath $r8bProj)) {
        Write-Step 'Building the r8brain resampler (r8bsrc64.dll)'
        $r8bArgs = @($r8bProj, '/restore', '/m', '/nologo', '/v:minimal', "/p:Configuration=$Configuration", '/p:Platform=x64')
        if ($PlatformToolset) { $r8bArgs += "/p:PlatformToolset=$PlatformToolset" }
        Invoke-Checked $vs.MSBuild $r8bArgs
        $r8bDll = Get-ChildItem -LiteralPath ([IO.Path]::Combine($Root, 'bin')) -Recurse -File -Filter 'r8bsrc64.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $r8bDll) { throw 'r8bsrc64.dll not built' }
        Copy-Item -LiteralPath $r8bDll.FullName -Destination $StudioStage -Force
        Write-Host "r8bsrc64.dll -> $StudioStage"
    } else {
        Add-Failure 'r8bsrc64.dll was not built (no Visual Studio C++ tools or no 3rdParty\r8b).'
    }
}

if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($StudioStage, $AppExe)))) {
    throw "$AppExe not found in $StudioStage. Run without -SkipCore first."
}

# ---------------------------------------------------------------- 5. the package

# NeoBuzzLive.exe, Gear\ and LinkHelper\ at the root; the program's own files (about a hundred libraries: Avalonia, SkiaSharp, the
# engine, PortAudio) in App\. The apphost starts App\ReBuzz.Studio.dll; BuzzPath (Global.BuzzPath) is the exe's folder when the
# assembly sits in App\ beside a Gear\.
Write-Step 'Packaging'
$pkgName = "NeoBuzz-Live-$Version-x64"
$pkgDir = [IO.Path]::Combine($DistDir, $pkgName)
if (Test-Path -LiteralPath $pkgDir) { Remove-Item -LiteralPath $pkgDir -Recurse -Force }
New-Dir $pkgDir
$appDir = [IO.Path]::Combine($pkgDir, 'App')
$licDir = [IO.Path]::Combine($pkgDir, 'Licenses')
New-Dir $appDir
New-Dir $licDir

$exclude = @('*.pdb', '*.ilk', '*.exp', '*.lib', '*.iobj', '*.ipdb')
$atRoot = @('Gear', 'LinkHelper', $AppExe)
foreach ($item in Get-ChildItem -LiteralPath $StudioStage -Force) {
    if ($item.Name -eq 'PortAudio-LICENSE.txt') { continue }                    # Licenses\ has it (below)
    $to = [IO.Path]::Combine($(if ($atRoot -contains $item.Name) { $pkgDir } else { $appDir }), $item.Name)
    if ($item.Name -eq 'PortAudio-BUILD.txt') { $to = [IO.Path]::Combine($licDir, $item.Name) }
    Copy-Item -LiteralPath $item.FullName -Destination $to -Recurse -Force
}
Get-ChildItem -LiteralPath $pkgDir -Recurse -File | Where-Object { $name = $_.Name; @($exclude | Where-Object { $name -like $_ }).Count -gt 0 } | Remove-Item -Force

# The apphost's embedded path to the app (a 1024 byte field, padded with zeros): App\ReBuzz.Studio.dll.
$hostExe = [IO.Path]::Combine($pkgDir, $AppExe)
$hostBytes = [IO.File]::ReadAllBytes($hostExe)
$latin = [Text.Encoding]::GetEncoding(28591)
$hostText = $latin.GetString($hostBytes)
$oldTarget = "ReBuzz.Studio.dll`0"
$at = $hostText.IndexOf($oldTarget, [StringComparison]::Ordinal)
if ($at -lt 0 -or $hostText.LastIndexOf($oldTarget, [StringComparison]::Ordinal) -ne $at) { throw "${AppExe}: the apphost's path to ReBuzz.Studio.dll was not found once" }
$newBytes = $latin.GetBytes('App\ReBuzz.Studio.dll')
for ($i = 0; $i -le $newBytes.Length; $i++) {
    if ($i -ge 'ReBuzz.Studio.dll'.Length + 1 -and $hostBytes[$at + $i] -ne 0) { throw "${AppExe}: the apphost's path field is shorter than App\ReBuzz.Studio.dll" }
}
[Array]::Copy($newBytes, 0, $hostBytes, $at, $newBytes.Length)
$hostBytes[$at + $newBytes.Length] = 0
[IO.File]::WriteAllBytes($hostExe, $hostBytes)
Write-Host "$AppExe starts App\ReBuzz.Studio.dll"

# The machines in Gear\: every DLL of Gear\Generators and Gear\Effects (a machine and the libraries it brings).
$inventory = New-Object System.Collections.Generic.List[string]
$inventory.Add("Machines in this package ($pkgName): the Neo machines and the Modern Pattern Editor, built from this repository")
$gearCounts = @()
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($pkgDir, 'Gear', $sub)
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $dlls = @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll' | Sort-Object Name)
    $inventory.Add('')
    $inventory.Add("[$sub] $($dlls.Count) DLLs")
    foreach ($d in $dlls) { $inventory.Add("  $($d.Name)") }
    $gearCounts += "$($dlls.Count) $($sub.ToLowerInvariant())"
}
$inventory | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'GEAR-INVENTORY.txt')) -Encoding UTF8
if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::notice title=Package::Gear: $($gearCounts -join ', ') DLLs. Build problems: $($Failures.Count)" }

$pkgBytes = (Get-ChildItem -LiteralPath $pkgDir -Recurse -File | Measure-Object Length -Sum).Sum
$shaText = if ($sha) { $sha } else { 'unknown' }
$info = @(
    "NeoBuzz Live build $Version (a fork of ReBuzz)",
    "Commit:      $shaText",
    "Built:       $((Get-Date).ToString('u'))",
    "Config:      $Configuration|x64",
    "Layout:      NeoBuzzLive.exe = the studio (Avalonia; an apphost that starts App\ReBuzz.Studio.dll); App\ = its program files; Gear\ = its machines",
    "Size:        $([math]::Round($pkgBytes / 1MB, 1)) MB unzipped",
    "Build problems: $($Failures.Count)"
)
$info += @($Failures | ForEach-Object { "  - $_" })
$info | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'BUILD-INFO.txt')) -Encoding UTF8

# ---------------------------------------------------------------- licences

# Licenses\NOTICE.txt: the components built from source here or bundled, whose licence text is in the repository, each copied beside it.
$components = @(
    [pscustomobject]@{ Name = 'NeoBuzz Live and ReBuzz (wasteddesign/ReBuzz), which it is based on'; Licence = 'MIT'; Text = 'LICENSE'; As = 'ReBuzz-LICENSE.txt'; File = $AppExe },
    [pscustomobject]@{ Name = 'Serilog (App\serilog.dll, from 3rdParty\serilog\serilog)'; Licence = 'Apache-2.0'; Text = '3rdParty\serilog\serilog\LICENSE'; As = 'Serilog-LICENSE.txt'; File = 'App\serilog.dll' },
    [pscustomobject]@{ Name = 'r8brain-free-src (App\r8bsrc64.dll, from 3rdParty\r8b\r8brain-free-src)'; Licence = 'MIT'; Text = '3rdParty\r8b\r8brain-free-src\LICENSE'; As = 'r8brain-free-src-LICENSE.txt'; File = 'App\r8bsrc64.dll' },
    [pscustomobject]@{ Name = 'PortAudio (App\portaudio.dll, built from source with CMake: MME, DirectSound, WASAPI and WDM-KS, no ASIO; see Licenses\PortAudio-BUILD.txt)'; Licence = 'MIT'; Text = ([IO.Path]::Combine($StudioStage, 'PortAudio-LICENSE.txt')); As = 'PortAudio-LICENSE.txt'; File = 'App\portaudio.dll' },
    [pscustomobject]@{ Name = 'Concentus 2.2.2 (App\Concentus.dll, the Opus preview; NuGet, from 3rdParty\concentus)'; Licence = 'BSD-3-Clause'; Text = '3rdParty\concentus\LICENSE'; As = 'Concentus-LICENSE.txt'; File = 'App\Concentus.dll' },
    [pscustomobject]@{ Name = 'Signalsmith Stretch 1.3.2 (App\neostretch.dll, the Pro warp mode; vendored in NeoStretch\third_party)'; Licence = 'MIT'; Text = 'NeoStretch\third_party\signalsmith-stretch\LICENSE.txt'; As = 'SignalsmithStretch-LICENSE.txt'; File = 'App\neostretch.dll' },
    [pscustomobject]@{ Name = 'Signalsmith Linear 0.6.6 (inside neostretch.dll; vendored in NeoStretch\third_party)'; Licence = 'MIT'; Text = 'NeoStretch\third_party\signalsmith-linear\LICENSE.txt'; As = 'SignalsmithLinear-LICENSE.txt'; File = 'App\neostretch.dll' },
    [pscustomobject]@{ Name = 'Basic Pitch (Spotify; the model nmp.onnx, embedded in ReBuzz.Engine.dll: Convert to MIDI, harmony; from 3rdParty\basic-pitch)'; Licence = 'Apache-2.0'; Text = '3rdParty\basic-pitch\LICENSE'; As = 'BasicPitch-LICENSE.txt'; File = 'App\ReBuzz.Engine.dll' },
    [pscustomobject]@{ Name = 'Basic Pitch NOTICE (the notice of the model above, which Apache-2.0 asks to travel with it)'; Licence = 'Apache-2.0'; Text = '3rdParty\basic-pitch\NOTICE'; As = 'BasicPitch-NOTICE.txt'; File = 'App\ReBuzz.Engine.dll' },
    [pscustomobject]@{ Name = 'Groove MIDI Dataset (Gillick, Roberts, Engel, Eck, Bamman; Magenta; https://magenta.tensorflow.org/datasets/groove): the drum classifier coefficients of Convert to MIDI, compiled into the app, were fitted on its train and validation splits; no audio or MIDI of it is shipped (from 3rdParty\groove-midi-dataset)'; Licence = 'CC-BY-4.0'; Text = '3rdParty\groove-midi-dataset\NOTICE'; As = 'GrooveMidiDataset-NOTICE.txt'; File = $AppExe },
    [pscustomobject]@{ Name = 'AW2 beat model (Auto-warp): the coefficients compiled into the app (BeatModelData.cs) were fitted on the Groove MIDI Dataset, GuitarSet, Candombe, AAM, Slakh2100 and the CC0 and CC BY 3.0 loops of the Freesound Loop Dataset (all CC BY or CC0); no audio is shipped (from 3rdParty\aw2-beat-model)'; Licence = 'CC-BY-4.0'; Text = '3rdParty\aw2-beat-model\NOTICE'; As = 'AW2BeatModel-NOTICE.txt'; File = $AppExe },
    [pscustomobject]@{ Name = 'AW2 beat model: the author and licence of each Freesound loop it was fitted on (CC0 and CC BY 3.0, from 3rdParty\aw2-beat-model)'; Licence = 'CC-BY-3.0'; Text = '3rdParty\aw2-beat-model\FSL10K-ATTRIBUTION.txt'; As = 'AW2BeatModel-FSL10K-ATTRIBUTION.txt'; File = $AppExe }
)
$notice = New-Object System.Collections.Generic.List[string]
$notice.Add("Licences in this package ($pkgName)")
$notice.Add('')
$notice.Add("NeoBuzz Live (neo-buzz repository, commit $shaText), the components it is built with, and their licences. Each text is in this folder.")
foreach ($c in $components) {
    $src = if ([IO.Path]::IsPathRooted($c.Text)) { $c.Text } else { [IO.Path]::Combine($Root, $c.Text) }
    if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, $c.File)))) { continue }
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination ([IO.Path]::Combine($licDir, $c.As)) -Force
        $notice.Add("  $($c.Name): $($c.Licence); text: Licenses\$($c.As)")
    } else {
        $notice.Add("  $($c.Name): $($c.Licence) (its text, $($c.Text), wasn't in this checkout)")
        Write-Warning "Licence text $($c.Text) not found (submodules not initialised?): $($c.Name) ships without it"
    }
}
if (Test-Path -LiteralPath ([IO.Path]::Combine($appDir, 'portaudio.dll'))) {
    $notice.Add('  App\: Avalonia, SkiaSharp and HarfBuzzSharp (MIT), the Inter typeface (OFL-1.1), NAudio (MIT) and libsndfile (LGPL-2.1, as a separate DLL), as their NuGet packages ship them.')
}
if (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, 'Gear', 'NeoPlug', 'neoplug.dll'))) {
    $notice.Add('  Gear\NeoPlug (the plug-in host behind Neo Plug): the VST3 SDK 3.8 (MIT) and the CLAP headers (MIT), statically; plug-ins are the user''s own and are not shipped.')
}
if (Test-Path -LiteralPath ([IO.Path]::Combine($pkgDir, 'LinkHelper', 'Link-LICENSE.md'))) {
    $notice.Add('  App\ReBuzzLink.exe (Ableton Link): GPL-2.0-or-later; text and source: LinkHelper\ (Link-LICENSE.md, Link-GNU-GPL-v2.0.md, main.cpp)')
}
$notice | Set-Content -LiteralPath ([IO.Path]::Combine($licDir, 'NOTICE.txt')) -Encoding UTF8
Write-Host "Licenses\: $((@(Get-ChildItem -LiteralPath $licDir -File | ForEach-Object Name)) -join ', ')"

$readme = @'
NeoBuzz Live - portable build
=============================

NeoBuzz Live is a fork of ReBuzz, by WDE and the ReBuzz contributors (MIT licence):
https://github.com/wasteddesign/ReBuzz

Start: unzip anywhere and run NeoBuzzLive.exe. It opens and saves NeoBuzz Live songs (.nbl); Preferences > General >
File types makes it the program double-clicked songs open with (this Windows user only; reversible). It keeps its
settings in %APPDATA%\NeoBuzz Live.

What is in this folder:
  NeoBuzzLive.exe   the program. A small launcher: it starts App\ReBuzz.Studio.dll, with this folder as its home.
  App\              the program's own files (Avalonia, SkiaSharp, the audio engine, PortAudio, the Link helper).
                    Nothing in it to touch; keep it beside NeoBuzzLive.exe.
  Gear\             the machines: the Neo instruments and effects, Neo Plug and Neo Plug FX (Gear\NeoPlug hosts
                    your CLAP and VST3 plug-ins) and the Modern Pattern Editor.
  LinkHelper\       the notes, licence and source of the Ableton Link helper (ReBuzzLink.exe, in App\).
  Licenses\         the licences of the components in this package (NOTICE.txt says which is which).
  README-FIRST.txt, BUILD-INFO.txt, GEAR-INVENTORY.txt   this file, how the package was built, every machine in it.

Audio: Preferences > Audio lists the installed ASIO drivers (the default when one is installed), then WASAPI: shared
with low latency (the default without ASIO), shared, and exclusive behind a switch. WDM-KS (PortAudio) under Advanced.
MIDI: WinMM inputs, outputs and clock.

AI agents: Preferences > Agent > Allow agents shows the line to connect Claude Code (the server is neobuzz-live).

Needs: the .NET 10 Desktop Runtime x64   https://dotnet.microsoft.com/download/dotnet/10.0
       and the Microsoft Visual C++ Redistributable x64 (latest)
                                          https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

GEAR-INVENTORY.txt lists every machine. BUILD-INFO.txt lists any part that was not built.
'@
@($readme -split "`r?`n") | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'README-FIRST.txt')) -Encoding UTF8
Write-Host "Package root: $((Get-ChildItem -LiteralPath $pkgDir -Force | ForEach-Object Name) -join ', ')"
Write-Host "App\: $((Get-ChildItem -LiteralPath $appDir -Recurse -File).Count) files; package $([math]::Round($pkgBytes / 1MB, 1)) MB unzipped"

if (-not $NoZip) {
    $zip = [IO.Path]::Combine($DistDir, "$pkgName.zip")
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    # CompressionLevel lives in System.IO.Compression; ZipFile in ...FileSystem. 5.1 needs both.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($pkgDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zipBytes = (Get-Item -LiteralPath $zip).Length
    Write-Host "Package: $zip ($([math]::Round($zipBytes / 1MB, 1)) MB)" -ForegroundColor Green
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::notice title=Package size::$pkgName.zip $zipBytes bytes; $pkgBytes bytes unzipped" }
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
    if ($env:GITHUB_ACTIONS -eq 'true' -and $env:GITHUB_STEP_SUMMARY) {
        $noticeFile = [IO.Path]::Combine($licDir, 'NOTICE.txt')
        $md = @('### Licence files in the zip', '') + @($licEntries | ForEach-Object { "- ``$_``" }) +
              @('', '<details><summary>Licenses/NOTICE.txt</summary>', '', '```') + @(Get-Content -LiteralPath $noticeFile) + @('```', '</details>')
        $md | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8
    }
} else {
    Write-Host "Package folder: $pkgDir" -ForegroundColor Green
}

if ($Failures.Count -gt 0) {
    Write-Warning "$($Failures.Count) build problem(s):"
    $Failures | ForEach-Object { Write-Warning "  $_" }
    if ($env:GITHUB_ACTIONS) { $Failures | ForEach-Object { Write-Host "::warning title=Build::$_" } }
}

# Don't let the last native tool's exit code (cmake, msbuild) leak out as the script's result.
exit 0
