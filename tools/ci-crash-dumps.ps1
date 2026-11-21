# Native host crash dumps on a Windows runner (RB1, docs/specs/machine-safety.md section 6), for .github/workflows/fork-package.yml.
#   ci-crash-dumps.ps1 register   Windows Error Reporting writes a full dump of ReBuzzEngine64.exe / ReBuzzEngine32.exe when they crash
#                                 (HKLM LocalDumps keys) into $RUNNER_TEMP\crashdumps; sets REBUZZ_CRASHDUMPS and REBUZZ_ENGINE_KEEP_WER
#                                 for the later steps (the host's own opt-out of Windows Error Reporting would leave no dump).
#   ci-crash-dumps.ps1 analyze    Lists the dumps; prints what Windows Error Reporting logged, and with cdb the exception, the faulting module,
#                                 the stack at the fault and the machines loaded; sets the step output found=true|false for the upload.
#   ci-crash-dumps.ps1 selftest   A program named ReBuzzEngine64.exe that fails fast, to see that the above works without a real crash.
param([Parameter(Mandatory = $true)][ValidateSet('register', 'analyze', 'selftest')][string]$Mode)

$ErrorActionPreference = 'Stop'
$dumps = if ($env:REBUZZ_CRASHDUMPS) { $env:REBUZZ_CRASHDUMPS } else { Join-Path $env:RUNNER_TEMP 'crashdumps' }

if ($Mode -eq 'register') {
    New-Item -ItemType Directory -Force $dumps | Out-Null
    foreach ($exe in 'ReBuzzEngine64.exe', 'ReBuzzEngine32.exe') {
        $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$exe"
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -Path $key -Name DumpFolder -PropertyType ExpandString -Value $dumps -Force | Out-Null
        New-ItemProperty -Path $key -Name DumpType -PropertyType DWord -Value 2 -Force | Out-Null      # 2: a full dump
        New-ItemProperty -Path $key -Name DumpCount -PropertyType DWord -Value 4 -Force | Out-Null
    }
    Write-Host ('LocalDumps keys: ' + ((Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' | ForEach-Object PSChildName) -join ', '))
    # The runner image has the Windows Error Reporting service stopped (self-test run 44955501496: WerSvc Stopped, Manual, and no dump of a crash);
    # WerFault needs it to write one, and Windows Error Reporting must not be switched off.
    $wer = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting'
    Write-Host ('Windows Error Reporting: Disabled=' + (Get-ItemProperty $wer -Name Disabled -ErrorAction SilentlyContinue).Disabled + ' DontShowUI=' + (Get-ItemProperty $wer -Name DontShowUI -ErrorAction SilentlyContinue).DontShowUI)
    Set-ItemProperty -Path $wer -Name Disabled -Value 0 -Type DWord
    Start-Service WerSvc
    Get-Service WerSvc | Format-Table Name, Status, StartType | Out-String | Write-Host
    "REBUZZ_CRASHDUMPS=$dumps" >> $env:GITHUB_ENV
    "REBUZZ_ENGINE_KEEP_WER=1" >> $env:GITHUB_ENV
    return
}

if ($Mode -eq 'selftest') {
    $d = Join-Path $env:RUNNER_TEMP 'dump-selftest'
    New-Item -ItemType Directory -Force $d | Out-Null
    Push-Location $d
    # The same crash the host had: the C runtime's abort(), a fast fail (0xc0000409) in ucrtbase, in a program named like the host. Two of them: as
    # it is (it inherits its parent's error mode, which a CI agent may have set to keep dialogs away: no report), and one that asks for reporting.
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
    $bat = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $variants = @(
        @{ Name = 'inherit'; Source = "#include <stdlib.h>`nint main(void) { abort(); return 0; }" },
        @{ Name = 'reset'; Source = "#include <stdlib.h>`n#include <windows.h>`nint main(void) { SetErrorMode(0); _set_abort_behavior(_CALL_REPORTFAULT, _CALL_REPORTFAULT); abort(); return 0; }" })
    foreach ($v in $variants) {
        New-Item -ItemType Directory -Force $v.Name | Out-Null
        Push-Location $v.Name
        Set-Content crash.c $v.Source
        cmd /c "`"$bat`" >nul && cl /nologo /Fe:ReBuzzEngine64.exe crash.c >nul"
        if ($LASTEXITCODE -ne 0) { throw 'cl failed' }
        $em = Add-Type -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint m);' -Name EM -Namespace Rb1 -PassThru
        $prevMode = $em::SetErrorMode(0); [void]$em::SetErrorMode($prevMode)
        Write-Host ("::group::variant $($v.Name): error mode of this process 0x{0:X}" -f $prevMode)
        & .\ReBuzzEngine64.exe
        Write-Host ('variant {0}: exit code 0x{1:X8}' -f $v.Name, $LASTEXITCODE)
        $global:LASTEXITCODE = 0
        for ($i = 0; $i -lt 15 -and -not (Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue); $i++) { Start-Sleep 2 }
        Write-Host ('dumps so far: ' + ((Get-ChildItem $dumps -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '))
        Write-Host '::endgroup::'
        Pop-Location
    }
    Pop-Location
    for ($i = 0; $i -lt 60 -and -not (Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue); $i++) { Start-Sleep 2 }
    Get-ChildItem $dumps -ErrorAction SilentlyContinue | Format-Table Name, Length | Out-String | Write-Host
    # If there is none: what Windows Error Reporting did see.
    foreach ($q in "$env:ProgramData\Microsoft\Windows\WER\ReportQueue", "$env:ProgramData\Microsoft\Windows\WER\ReportArchive", "$env:LOCALAPPDATA\CrashDumps", "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportQueue") {
        Write-Host ("$q : " + ((Get-ChildItem $q -ErrorAction SilentlyContinue | ForEach-Object Name | Select-Object -First 8) -join ', '))
    }
    Get-WinEvent -LogName Application -MaxEvents 12 -ErrorAction SilentlyContinue | ForEach-Object { '{0} {1} {2}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.ProviderName, (($_.Message -split "`r?`n")[0]) } | Write-Host
    return
}

# analyze
$files = @(Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue)
if (-not $files) {
    Write-Host 'No dump of ReBuzzEngine64.exe or ReBuzzEngine32.exe was written in this run.'
    "found=false" >> $env:GITHUB_OUTPUT
    return
}
"found=true" >> $env:GITHUB_OUTPUT
$cdb = @("${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe", "$env:ProgramFiles\Windows Kits\10\Debuggers\x64\cdb.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cdb) { $c = Get-Command cdb.exe -ErrorAction SilentlyContinue; if ($c) { $cdb = $c.Source } }
Write-Host "cdb: $cdb"
$sym = Join-Path $env:RUNNER_TEMP 'symbols'
# What Windows Error Reporting itself recorded (the faulting module, the exception code and the offset in it), with or without cdb.
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error' } -MaxEvents 50 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'ReBuzzEngine' } | Select-Object -First 5
foreach ($e in $events) { Write-Host ("::notice title=Application Error (ReBuzzEngine)::" + (($e.Message -split "`r?`n" | Select-Object -First 12) -join ' | ')) }
foreach ($f in $files) {
    Write-Host "::group::$($f.Name) ($([math]::Round($f.Length / 1MB)) MB)"
    if ($cdb) {
        $txt = Join-Path $dumps ($f.BaseName + '.txt')
        & $cdb -z $f.FullName -y "srv*$sym*https://msdl.microsoft.com/download/symbols" -c ".lines -e; .ecxr; .echo ===STACK; kv 40; .echo ===ANALYZE; !analyze -v; .echo ===MODULES; lm; q" *> $txt
        $lines = @(Get-Content $txt)
        $lines | Select-String -Pattern 'FAULTING_MODULE|MODULE_NAME|IMAGE_NAME|ExceptionCode|EXCEPTION_CODE|FAILURE_BUCKET_ID|SYMBOL_NAME' | Select-Object -First 12 | ForEach-Object { Write-Host $_.Line }
        $from = [array]::IndexOf($lines, '===STACK'); if ($from -lt 0) { $from = 0 }
        $lines | Select-Object -Skip $from -First 60 | ForEach-Object { Write-Host $_ }
        Write-Host '--- machine and runtime modules loaded (not the system folder):'
        $lines | Select-String -Pattern '\\(ReBuzzTestData|Gear|bin64|bin32)\\|\\Machines\\' | Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
        Write-Host "::notice title=Native host crash ($($f.Name))::$((($lines | Select-String -Pattern 'FAULTING_MODULE|FAILURE_BUCKET_ID|SYMBOL_NAME' | Select-Object -First 3 | ForEach-Object { $_.Line.Trim() }) -join ' | '))"
    }
    else { Write-Host 'cdb.exe is not on this runner: the dump is in the artifact, to open with WinDbg.' }
    Write-Host '::endgroup::'
}
