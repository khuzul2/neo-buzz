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
    dotnet new console -n ReBuzzEngine64 --force | Out-Null
    # RaiseFailFastException goes to Windows Error Reporting, as the C runtime's fast fail does (Environment.FailFast ends the process itself).
    @'
using System.Runtime.InteropServices;
System.Console.WriteLine("failing fast");
RaiseFailFastException(System.IntPtr.Zero, System.IntPtr.Zero, 0);
[DllImport("kernel32.dll")] static extern void RaiseFailFastException(System.IntPtr record, System.IntPtr context, int flags);
'@ | Set-Content ReBuzzEngine64/Program.cs
    dotnet publish ReBuzzEngine64 -c Release -o out | Out-Null
    & .\out\ReBuzzEngine64.exe
    Write-Host ('exit code 0x{0:X8}' -f $LASTEXITCODE)
    $global:LASTEXITCODE = 0
    Pop-Location
    for ($i = 0; $i -lt 90 -and -not (Get-ChildItem $dumps -Filter *.dmp -ErrorAction SilentlyContinue); $i++) { Start-Sleep 2 }
    Get-ChildItem $dumps -ErrorAction SilentlyContinue | Format-Table Name, Length | Out-String | Write-Host
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
