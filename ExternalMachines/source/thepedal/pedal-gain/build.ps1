# build.ps1 — Build Pedal Gain and deploy to ReBuzz
#
# Usage:
#   .\build.ps1                                  # default install path
#   .\build.ps1 -BuzzDir "D:\MyReBuzz"           # custom install path

param(
    [string]$BuzzDir = "C:\Program Files\ReBuzz"
)

Write-Host "Building Pedal Gain -> $BuzzDir\Gear\Effects\" -ForegroundColor Cyan

dotnet build PedalGain.csproj -c Release /p:BuzzDir=$BuzzDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nBuild succeeded.  Pedal Gain.NET.dll is ready in ReBuzz Effects." -ForegroundColor Green
    Write-Host "Restart ReBuzz and look for 'Pedal Gain' under Effects." -ForegroundColor Green
} else {
    Write-Host "`nBuild failed — see errors above." -ForegroundColor Red
}
