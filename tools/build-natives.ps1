<#
.SYNOPSIS
    Builds the classic native machines that have sources in ExternalMachines (the Buzztrax collection,
    docs/specs/mcp-server.md §12.4, U7) with the Visual Studio C++ compiler on this machine.

.DESCRIPTION
    The machines and how each builds are the "natives" list of the buzztrax-buzzmachines entry in
    ExternalMachines\machines.json: its sources, defines, libraries and extra files (presets, help). The
    compiler flags are the Buzztrax makefiles' (common.nmake, Release): /MT, no optimisation switch (so
    cl's default, /Od), WIN32 [WIN64], _WINDLL, __MSVC__, _X86_ or _AMD64_; -Flags adds to them.

    For each platform it builds Buzztrax's MDK (common\mdk, static) and links each machine against it
    and against ReBuzz's own dsplib import library (ReBuzzEngine\dsplib for x86, dsplib64 for x64): the
    DLL the machines then load is the dsplib.dll in ReBuzz's bin32 or bin64, as the shipped binaries do.

    Output: <OutDir>\<platform>\Gear\<Generators|Effects>\<gear><Suffix>[.x64].dll (x64 DLLs end in
    .x64.dll, which is how ReBuzz knows to run them in its 64-bit host), the machine's extra files, and
    build.log. Exit code 1 when a machine didn't build.

.EXAMPLE
    ./tools/build-natives.ps1 -OutDir build/natives
.EXAMPLE
    ./tools/build-natives.ps1 -OutDir build/natives-o2 -Flags '/O2' -Suffix ' O2' -Platforms x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Platforms = @('x86', 'x64'),
    [string]$Flags = '',
    [string]$Suffix = '',
    [string[]]$Only,
    [string]$VsPath,
    [switch]$NoExtras
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$manifest = Get-Content -LiteralPath (Join-Path $Root 'ExternalMachines/machines.json') -Raw | ConvertFrom-Json
$bt = $manifest.machines | Where-Object { $_.id -eq 'buzztrax-buzzmachines' } | Select-Object -First 1
if (-not $bt) { throw 'machines.json has no buzztrax-buzzmachines entry' }
$src = Join-Path $Root ('ExternalMachines/' + $bt.path)
$machines = @($bt.natives | Where-Object { -not $Only -or $Only -contains $_.gear })
if ($machines.Count -eq 0) { throw 'no machines to build' }

if (-not $VsPath) {
    $vswhere = [IO.Path]::Combine(${env:ProgramFiles(x86)}, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe')
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found: install Visual Studio with the C++ workload' }
    $VsPath = (& $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $VsPath) { throw 'No Visual Studio with the C++ x86/x64 tools found' }
}

function Q([string]$s) { '"' + $s + '"' }
# A folder for cl's /Fo: two backslashes before the closing quote (one would escape it).
function QDir([string]$s) { '"' + $s + '\\"' }

$failed = New-Object System.Collections.Generic.List[string]
$built = 0
foreach ($platform in $Platforms) {
    $vcvars = Join-Path $VsPath ("VC\Auxiliary\Build\vcvars{0}.bat" -f $(if ($platform -eq 'x86') { '32' } else { '64' }))
    if (-not (Test-Path -LiteralPath $vcvars)) { throw "$vcvars not found" }
    $out = Join-Path $OutDir $platform
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    $obj = Join-Path $out 'obj'
    New-Item -ItemType Directory -Force -Path $obj | Out-Null
    $common = Join-Path $src 'common'
    $archDefs = if ($platform -eq 'x86') { '/D_X86_' } else { '/DWIN64 /D_AMD64_' }
    $base = "/nologo /MT /DWIN32 $archDefs /D_WINDLL /D__MSVC__ /I$(Q $common) $Flags"
    $dsplib = Join-Path $Root $(if ($platform -eq 'x86') { 'ReBuzzEngine\dsplib\dsplib.lib' } else { 'ReBuzzEngine\dsplib64\dsplib.lib' })
    $mdkObj = Join-Path $obj 'mdk'
    $mdk = Join-Path $obj 'mdk.lib'

    $lines = @('@echo off', "call $(Q $vcvars) >nul || exit /b 2", 'set RC=0')
    New-Item -ItemType Directory -Force -Path $mdkObj | Out-Null
    $lines += "echo ==== MDK ($platform)"
    $lines += "cl $base /c $(Q (Join-Path $common 'mdk\mdkimp.cpp')) /Fo$(QDir $mdkObj) || set RC=1"
    $lines += "lib /nologo /OUT:$(Q $mdk) $(Q (Join-Path $mdkObj 'mdkimp.obj')) || set RC=1"
    foreach ($m in $machines) {
        $dir = Join-Path $src $m.dir
        $mobj = Join-Path $obj ($m.gear -replace '[^A-Za-z0-9]', '_')
        New-Item -ItemType Directory -Force -Path $mobj | Out-Null
        $gearDir = Join-Path $out ('Gear\' + $m.gearFolder)
        New-Item -ItemType Directory -Force -Path $gearDir | Out-Null
        $dll = Join-Path $gearDir ($m.gear + $Suffix + $(if ($platform -eq 'x64') { '.x64.dll' } else { '.dll' }))
        $defs = (@($m.defines) | Where-Object { $_ } | ForEach-Object { "/D$_" }) -join ' '
        $srcs = (@($m.sources) | ForEach-Object { Q (Join-Path $dir $_) }) -join ' '
        $libs = @()
        foreach ($l in @($m.libs)) {
            switch ($l) {
                'mdk' { $libs += (Q $mdk) }
                'dsplib' { $libs += (Q $dsplib) }
                $null { }
                default { $libs += "$l.lib" }
            }
        }
        $lines += "echo ==== $($m.gear) ($platform)"
        $lines += "cl $base $defs $srcs /Fo$(QDir $mobj) /LD /Fe$(Q $dll) $($libs -join ' ') /link /NOLOGO /IMPLIB:$(Q (Join-Path $mobj 'machine.lib')) || set RC=1"
    }
    $lines += 'exit /b %RC%'
    $cmd = Join-Path $out 'build.cmd'
    $lines | Set-Content -LiteralPath $cmd -Encoding Ascii
    $log = Join-Path $out 'build.log'
    Write-Host "==> Building $($machines.Count) native machines ($platform) $Flags"
    & cmd.exe /d /c (Q $cmd) *>&1 | Tee-Object -FilePath $log | Out-Host
    foreach ($m in $machines) {
        $gearDir = Join-Path $out ('Gear\' + $m.gearFolder)
        $dll = Join-Path $gearDir ($m.gear + $Suffix + $(if ($platform -eq 'x64') { '.x64.dll' } else { '.dll' }))
        if (Test-Path -LiteralPath $dll) {
            $built++
            if (-not $NoExtras -and (Get-Member -InputObject $m -Name files -MemberType NoteProperty)) {
                foreach ($p in $m.files.PSObject.Properties) {
                    Copy-Item -LiteralPath (Join-Path (Join-Path $src $m.dir) $p.Name) -Destination (Join-Path $gearDir $p.Value) -Force
                }
            }
        } else {
            $failed.Add("$($m.gear) ($platform)")
        }
    }
    # cl leaves the import library and exports file beside each DLL with /LD: not machines.
    Get-ChildItem -LiteralPath (Join-Path $out 'Gear') -Recurse -File -Include '*.exp', '*.lib' -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Host "Built $built of $($machines.Count * $Platforms.Count) native machine DLLs into $OutDir"
if ($failed.Count -gt 0) {
    Write-Warning "Not built: $($failed -join ', ')"
    exit 1
}
exit 0
