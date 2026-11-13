<#
.SYNOPSIS
    Publishes the studio (ReBuzz.Studio, Avalonia) for Windows x64: NeoBuzz Live Studio (preview).
    (docs/specs/ux-overhaul.md P6)

.DESCRIPTION
    Framework-dependent like the WPF app (it needs the .NET 10 runtime, which the Desktop runtime includes), with its
    own executable, NeoBuzzLiveStudio.exe, and beside it: its Gear (the Neo machines with their Avalonia editors, the
    Modern Pattern Editor, the Pedal machines), PortAudio built from source (tools/build-portaudio.ps1) and libsndfile.
    build.ps1 then adds what the package shares with the WPF app (the native machine hosts and DLLs) and puts the
    folder in the package as Studio\.

      ./tools/publish-studio-windows.ps1 -Out <folder> [-SkipPortAudio]
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Configuration = 'Release',
    [switch]$SkipPortAudio
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
}

$Root = Split-Path -Parent $PSScriptRoot
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Write-Host '==> Publishing ReBuzz.Studio (win-x64, framework-dependent)'
Invoke-Checked dotnet @('publish', [IO.Path]::Combine($Root, 'ReBuzz.Studio', 'ReBuzz.Studio.csproj'),
    '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '-p:UseAppHost=true', '-p:ReBuzzCleanBuild=true',
    '-nologo', '-v:minimal', '-o', $Out)

# The executable people start: the apphost, renamed (it starts ReBuzz.Studio.dll, whose name it carries).
$apphost = [IO.Path]::Combine($Out, 'ReBuzz.Studio.exe')
if (-not (Test-Path -LiteralPath $apphost)) { throw "ReBuzz.Studio.exe not published in $Out" }
Move-Item -LiteralPath $apphost -Destination ([IO.Path]::Combine($Out, 'NeoBuzzLiveStudio.exe')) -Force

# libsndfile (wave import, BuzzGUI.Common's binding): the x64 build the WPF app ships.
Copy-Item -LiteralPath ([IO.Path]::Combine($Root, '3rdParty', 'libsndfile-1.dll')) -Destination $Out -Force

if (-not $SkipPortAudio) {
    & ([IO.Path]::Combine($Root, 'tools', 'build-portaudio.ps1')) -Out $Out -Configuration $Configuration
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw 'build-portaudio.ps1 failed' }
}
Write-Host "NeoBuzzLiveStudio.exe -> $Out"
