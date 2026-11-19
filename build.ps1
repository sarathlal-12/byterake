# Builds Byterake: runs the tests, publishes one self-contained Byterake.exe into .\dist,
# then builds the installer into .\Output (if Inno Setup is installed).
# Usage (PowerShell, from this folder):  .\build.ps1
# Needs the .NET 10 SDK. The finished app needs nothing installed.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$version = "1.0.0"

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test tests\Byterake.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed - not building." }

Write-Host "Publishing single-file exe..." -ForegroundColor Cyan
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src\Byterake.App -c Release -o dist -p:Version=$version
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Remove-Item dist\*.pdb -ErrorAction SilentlyContinue
Copy-Item catalog.json dist\catalog.example.json
$exe = Get-Item dist\Byterake.exe
Write-Host ("Exe: {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "G:\APPS\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { Write-Host "Inno Setup not found - skipped the installer." -ForegroundColor Yellow; return }

Write-Host "Building installer..." -ForegroundColor Cyan
& $iscc /Q "/DMyAppVersion=$version" installer\installer.iss
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
$setup = Get-Item "Output\Byterake_Setup_v$version.exe"
Write-Host ("Done: {0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
