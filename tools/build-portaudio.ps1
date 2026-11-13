<#
.SYNOPSIS
    Builds PortAudio for Windows from source with CMake (docs/specs/ux-overhaul.md P6, the studio's Windows preview).

.DESCRIPTION
    PortAudio (MIT licence) is the studio's audio on every OS (ReBuzz/Platform/PortAudio.cs). On Windows this builds
    it as portaudio.dll for x64 with the host APIs MME (WMME), DirectSound, WASAPI and WDM-KS, and NOT ASIO: the ASIO
    SDK's licence doesn't allow redistributing a build that includes it. The CRT is linked statically, so the DLL needs
    no Visual C++ redistributable. The source is a pinned release tag, cloned shallow into build\portaudio\src.

    Out gets portaudio.dll, PortAudio-LICENSE.txt (PortAudio's own licence text, from its source) and
    PortAudio-BUILD.txt (what was built).

      ./tools/build-portaudio.ps1 -Out <folder> [-Tag v19.7.0] [-Source <an existing checkout>]
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Tag = 'v19.7.0',
    [string]$Source,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
}

$Root = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::Combine($Root, 'build', 'portaudio')
$src = if ($Source) { $Source } else { [IO.Path]::Combine($work, 'src') }
$bld = [IO.Path]::Combine($work, 'build')
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
New-Item -ItemType Directory -Force -Path $Out | Out-Null

if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($src, 'CMakeLists.txt')))) {
    Write-Host "Cloning PortAudio $Tag"
    if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }
    Invoke-Checked git @('clone', '--quiet', '--depth', '1', '--branch', $Tag, 'https://github.com/PortAudio/portaudio.git', $src)
}
$commit = (& git -C $src rev-parse HEAD 2>$null)
Write-Host "PortAudio $Tag ($commit)"

if (Test-Path -LiteralPath $bld) { Remove-Item -LiteralPath $bld -Recurse -Force }
Invoke-Checked cmake @(
    '-S', $src, '-B', $bld, '-A', 'x64',
    '-DBUILD_SHARED_LIBS=ON', '-DPA_BUILD_SHARED_LIBS=ON',
    '-DPA_BUILD_EXAMPLES=OFF', '-DPA_BUILD_TESTS=OFF',
    '-DPA_USE_WMME=ON', '-DPA_USE_DS=ON', '-DPA_USE_WASAPI=ON', '-DPA_USE_WDMKS=ON', '-DPA_USE_ASIO=OFF',
    '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
Invoke-Checked cmake @('--build', $bld, '--config', $Configuration, '--parallel')

# What got built: the DLL (CMake names it portaudio_x64.dll for MSVC x64), and which host APIs are in it.
$dll = Get-ChildItem -LiteralPath $bld -Recurse -File -Filter 'portaudio*.dll' | Where-Object { $_.FullName -match [regex]::Escape($Configuration) } | Select-Object -First 1
if (-not $dll) { throw "portaudio.dll not built in $bld" }
$objects = @(Get-ChildItem -LiteralPath $bld -Recurse -File -Filter '*.obj' | ForEach-Object { $_.Name })
$apis = [ordered]@{ 'MME' = 'pa_win_wmme'; 'DirectSound' = 'pa_win_ds'; 'WASAPI' = 'pa_win_wasapi'; 'WDM-KS' = 'pa_win_wdmks'; 'ASIO' = 'pa_asio' }
$built = @()
foreach ($name in $apis.Keys) {
    $has = [bool]($objects | Where-Object { $_ -like "$($apis[$name])*" })
    if ($has) { $built += $name }
    Write-Host ("  host API {0,-12} {1}" -f $name, $(if ($has) { 'built' } else { 'not built' }))
}
foreach ($need in 'MME', 'DirectSound', 'WASAPI', 'WDM-KS') { if ($built -notcontains $need) { throw "PortAudio was built without $need" } }
if ($built -contains 'ASIO') { throw 'PortAudio was built with ASIO (the SDK licence forbids shipping it)' }

Copy-Item -LiteralPath $dll.FullName -Destination ([IO.Path]::Combine($Out, 'portaudio.dll')) -Force
$licence = [IO.Path]::Combine($src, 'LICENSE.txt')
if (-not (Test-Path -LiteralPath $licence)) { throw "PortAudio's LICENSE.txt is missing in $src" }
Copy-Item -LiteralPath $licence -Destination ([IO.Path]::Combine($Out, 'PortAudio-LICENSE.txt')) -Force
@(
    "PortAudio $Tag ($commit), built from source with CMake for Windows x64 ($($dll.Name) shipped as portaudio.dll).",
    "Host APIs: $($built -join ', '). No ASIO: the ASIO SDK's licence doesn't allow redistributing a build with it.",
    'Statically linked C runtime. Source: https://github.com/PortAudio/portaudio (MIT licence, PortAudio-LICENSE.txt).'
) | Set-Content -LiteralPath ([IO.Path]::Combine($Out, 'PortAudio-BUILD.txt')) -Encoding UTF8
Write-Host "portaudio.dll -> $Out"
