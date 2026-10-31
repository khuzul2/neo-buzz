<#
.SYNOPSIS
    Builds ReBuzz (this fork) plus every buildable external machine, and packs a
    ready-to-run zip with all machines already in place.

.DESCRIPTION
    1. Finds Visual Studio (MSBuild + C++ x64 tools) and the .NET 10 SDK.
    2. Initialises git submodules.
    3. Builds ReBuzz.sln (Release|x64) into bin\Release\x64\net10.0-windows10.0.26100.0.
       This includes ReBuzz's own machines and copies RunTime\Gear (73 prebuilt machines).
    4. Builds ExternalMachines: every 'rebuzz-managed' entry in ExternalMachines\machines.json
       (dotnet build). With -NativeMachines also the Buzztrax nmake subset (FSM, Fuzzpilz,
       Matilde, Oomek) as x64; off by default because those 2000s sources don't compile with
       current MSVC headers yet, and upstream Gear already ships all but one of them.
    5. Merges prebuilt DLLs from ExternalMachines\binaries.
    6. Writes GEAR-INVENTORY.txt, BUILD-INFO.txt and README-FIRST.txt, then zips to dist\.

    Precedence: upstream machines > machines built from source > prebuilt binaries.
    Nothing ever overwrites a file already in the Gear folder, and "Foo.dll" and
    "Foo.x64.dll" count as the same machine. Files this script added on a previous run are
    removed first, so re-runs (and -SkipCore) start from the upstream Gear set.

    A machine that fails to build is reported and skipped (use -StrictMachines to fail).

    Run from a normal PowerShell prompt on Windows (Windows PowerShell 5.1 or pwsh 7).
    For -NativeMachines, clone the repo to a path without spaces (the Buzztrax makefiles
    don't quote paths).

.PARAMETER Version
    Version label for the package. Default: yyyy.MM.dd-<git short sha>.

.PARAMETER PlatformToolset
    Override the C++ toolset. The projects ask for v145 (Visual Studio 2026). When an
    older Visual Studio is found and this is not set, v143 is used with a warning.

.PARAMETER SkipCore
    Reuse an existing ReBuzz build (machines + package only).

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -SkipCore
.EXAMPLE
    .\build.ps1 -NativeMachines
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
    [switch]$NativeMachines,
    [switch]$SkipBinaries,
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
$needVs = (-not $SkipCore) -or $NativeMachines
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

if (-not (Test-Path -LiteralPath ([IO.Path]::Combine($OutDir, 'ReBuzz.exe')))) {
    throw "ReBuzz.exe not found in $OutDir. Run without -SkipCore first."
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
    foreach ($asm in 'ReBuzz.dll', 'BuzzGUI.Interfaces.dll', 'BuzzGUI.Common.dll', 'NAudio.WinMM.dll') {
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

# ---------------------------------------------------------------- 4b. native Buzztrax machines

if ($NativeMachines) {
    $bt = $manifest.machines | Where-Object { $_.kind -eq 'native-nmake' } | Select-Object -First 1
    if ($bt) {
        Write-Step "Building Buzztrax machines ($((Get-Prop $bt 'nmakeBuilds') -join ', ')) as x64"
        if ($Root.Contains(' ')) {
            # The Buzztrax makefiles use unquoted install paths.
            Add-MachineFailure "buzztrax: skipped because the repo path contains spaces ($Root). Clone to a path without spaces."
            $bt = $null
        }
    }
    if ($bt) {
        $btDir = [IO.Path]::Combine($ExtRoot, ($bt.path -replace '/', '\'))
        $btStage = [IO.Path]::Combine($StageRoot, 'buzztrax')
        if (Test-Path -LiteralPath $btStage) { Remove-Item -LiteralPath $btStage -Recurse -Force }
        New-Dir $btStage
        if (-not (Test-Path -LiteralPath $vs.VcVars)) { throw "vcvars64.bat not found at $($vs.VcVars)" }
        # A temp .cmd avoids PowerShell/cmd quoting differences across PowerShell versions.
        # /K keeps going past a machine that fails to compile; whatever built is installed.
        $cmdFile = [IO.Path]::Combine($StageRoot, 'build-buzztrax.cmd')
        @(
            '@echo off',
            "call `"$($vs.VcVars)`" >nul || exit /b 1",
            'set PLATFORM=x64',
            "cd /d `"$btDir`" || exit /b 1",
            'set RC=0',
            'nmake /nologo /K -f makefile.nmake PLATFORM=x64 CONFIGURATION=Release all || set RC=1',
            "nmake /nologo /K -f makefile.nmake PLATFORM=x64 CONFIGURATION=Release `"INSTALLDIR=$btStage`" install || set RC=1",
            'exit /b %RC%'
        ) | Set-Content -LiteralPath $cmdFile -Encoding Oem   # cmd.exe reads batch files in the OEM code page
        & cmd.exe /d /c "`"$cmdFile`""
        if ($LASTEXITCODE -ne 0) {
            Add-MachineFailure "buzztrax: some machines failed to build (see nmake output above); the rest are included"
        }
        foreach ($sub in 'Generators', 'Effects') {
            $dir = [IO.Path]::Combine($btStage, 'Gear', $sub)
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            # ReBuzz loads a native DLL in its 64-bit host only when the name ends in "x64.dll";
            # anything else goes to the 32-bit host. Presets and help keep their base names.
            Get-ChildItem -LiteralPath $dir -File -Filter '*.dll' |
                Where-Object { $_.Name -notlike '*.x64.dll' } |
                ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName ($_.BaseName + '.x64.dll') }
            $n = Copy-IntoGear -Files @(Get-ChildItem -LiteralPath $dir -File) -GearFolder $sub -Origin 'external:buzztrax'
            Write-Host "Added $n Buzztrax file(s) to Gear\$sub (the rest are already in upstream Gear)"
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

# ---------------------------------------------------------------- 6. package

Write-Step 'Packaging'
$pkgName = "ReBuzz-$Version-x64"
$pkgDir = [IO.Path]::Combine($DistDir, $pkgName)
if (Test-Path -LiteralPath $pkgDir) { Remove-Item -LiteralPath $pkgDir -Recurse -Force }
New-Dir $pkgDir

$exclude = @('*.pdb', '*.ilk', '*.exp', '*.lib', '*.iobj', '*.ipdb')
Get-ChildItem -LiteralPath $OutDir -Recurse -File | Where-Object {
    $name = $_.Name
    -not ($exclude | Where-Object { $name -like $_ })
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

# Inventory of machine DLLs and where each came from.
$inventory = New-Object System.Collections.Generic.List[string]
$inventory.Add("Machines in this package ($pkgName)")
$inventory.Add('Origin: rebuzz = upstream ReBuzz (RunTime\Gear or its own machines), external:<id> = built from ExternalMachines, binaries[:repo] = prebuilt from ExternalMachines\binaries')
foreach ($sub in 'Generators', 'Effects') {
    $dir = [IO.Path]::Combine($pkgDir, 'Gear', $sub)
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $dlls = @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll' | Sort-Object Name)
    $inventory.Add('')
    $inventory.Add("[$sub] $($dlls.Count) DLLs")
    foreach ($d in $dlls) {
        $origin = $Origins["$sub\$($d.Name)"]
        if (-not $origin) { $origin = 'unknown' }
        $inventory.Add(('  {0,-48} {1}' -f $d.Name, $origin))
    }
}
$inventory | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'GEAR-INVENTORY.txt')) -Encoding UTF8

# On CI, the inventory in short as an annotation (the package itself is an artifact to download).
# A DLL is a machine if it is a managed one (it references MachineDecl) or a native Buzz machine
# (it exports GetInfo); others in Gear are libraries machines use. "Foo" and "Foo.x64" count once.
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

if ($env:GITHUB_ACTIONS -eq 'true') {
    try { Add-Type -AssemblyName System.Reflection.Metadata } catch { }
    $parts = @()
    $kinds = @{}
    $byOrigin = @{}
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($sub in 'Generators', 'Effects') {
        $dir = [IO.Path]::Combine($pkgDir, 'Gear', $sub)
        $dlls = if (Test-Path -LiteralPath $dir) { @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll') } else { @() }
        $machines = 0
        foreach ($d in $dlls) {
            $kind = Get-MachineKind $d.FullName
            $kinds[$kind] = 1 + [int]$kinds[$kind]
            if ($kind -ne 'managed' -and $kind -ne 'native') { continue }
            if (-not $seen.Add("$sub\" + ($d.BaseName -replace '\.x64$', ''))) { continue }
            $machines++
            $origin = $Origins["$sub\$($d.Name)"]
            $where = if (-not $origin) { 'unknown' } elseif ($origin -like 'external:*') { 'built from ExternalMachines' } elseif ($origin -like 'binaries*') { 'prebuilt binaries' } else { 'upstream and own' }
            $byOrigin[$where] = 1 + [int]$byOrigin[$where]
        }
        $parts += "$machines $($sub.ToLowerInvariant())"
    }
    $total = $seen.Count
    $originText = ($byOrigin.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', '
    $files = ($kinds.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', '
    $vst = [IO.Path]::Combine($pkgDir, 'Gear', 'vst')
    $vstCount = if (Test-Path -LiteralPath $vst) { @(Get-ChildItem -LiteralPath $vst -Recurse -File -Filter '*.dll').Count } else { 0 }
    Write-Host "::notice title=Package::$total machines in Gear: $($parts -join ', ') ($originText). DLLs: $files. Gear\vst: $vstCount DLLs. Machine build failures: $($Failures.Count)"
    # GEAR-INVENTORY.txt itself, whole (a workflow command is one line: newlines escaped).
    $text = ($inventory -join "`n").Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Host "::notice title=Gear inventory::$text"
}

$toolsetText = if ($PlatformToolset) { $PlatformToolset } else { 'project default (v145)' }
$shaText = if ($sha) { $sha } else { 'unknown' }
$info = @(
    "ReBuzz fork build $Version",
    "Commit:      $shaText",
    "Built:       $((Get-Date).ToString('u'))",
    "Config:      $Configuration|x64",
    "C++ toolset: $toolsetText",
    "VC++ 2010 x64 runtime (bin64\msvcr100.dll) bundled: $(if ($crtBundled) { 'yes' } else { 'NO - install the VC++ 2010 SP1 x64 redistributable' })",
    "Machine build failures: $($Failures.Count)"
)
$info += @($Failures | ForEach-Object { "  - $_" })
$info | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'BUILD-INFO.txt')) -Encoding UTF8

@'
ReBuzz (fork) - portable build
==============================

Unzip anywhere and run ReBuzz.exe. The first start scans all machines in Gear\, which takes a while.

Needs (same as upstream ReBuzz):
  1. .NET 10 Desktop Runtime x64   https://dotnet.microsoft.com/download/dotnet/10.0
  2. Microsoft Visual C++ Redistributable, x86 AND x64 (latest)
                                    https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
     bin64\dsplib.dll also needs the x64 VC++ 2010 runtime (msvcr100.dll). It is bundled in
     bin64 unless BUILD-INFO.txt says otherwise.
  3. Windows MIDI Services SDK Runtime and Tools   https://microsoft.github.io/MIDI/

GEAR-INVENTORY.txt lists every machine and where it came from. BUILD-INFO.txt lists any machine
that failed to build. Machine licenses vary; see ExternalMachines\README.md in the source repo.
'@ | Set-Content -LiteralPath ([IO.Path]::Combine($pkgDir, 'README-FIRST.txt')) -Encoding UTF8

if (-not $NoZip) {
    $zip = [IO.Path]::Combine($DistDir, "$pkgName.zip")
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    # CompressionLevel lives in System.IO.Compression; ZipFile in ...FileSystem. 5.1 needs both.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($pkgDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "Package: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
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
