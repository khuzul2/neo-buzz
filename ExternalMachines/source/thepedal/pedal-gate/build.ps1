# build.ps1 – Build and deploy Pedal Gate.NET.dll
#
# Usage:
#   .\build.ps1                          # installs to C:\Program Files\ReBuzz\Gear\Effects\
#   .\build.ps1 -BuzzDir "D:\ReBuzz"     # custom ReBuzz location
#
# Requirements:
#   • .NET 10 SDK  (https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
#   • ReBuzz installed at BuzzDir

param(
    [string]$BuzzDir = "C:\Program Files\ReBuzz"
)

Write-Host "Building Pedal Gate.NET ..." -ForegroundColor Cyan
Write-Host "  BuzzDir  : $BuzzDir"
Write-Host "  OutputDir: $BuzzDir\Gear\Effects\"
Write-Host ""

dotnet build PedalGate.csproj -c Release /p:BuzzDir="$BuzzDir"

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Done! Restart ReBuzz and look for 'Pedal Gate' under Effects." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Build failed — check errors above." -ForegroundColor Red
    exit 1
}
