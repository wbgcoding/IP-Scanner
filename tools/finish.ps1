# One-shot finisher: generate icon -> build -> test -> publish single-file exe.
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tools\finish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host '[1/4] Generating icon ...' -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-icon.ps1')

Write-Host '[2/4] Building solution ...' -ForegroundColor Cyan
dotnet build IP-Scanner.slnx -c Debug -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host '[3/4] Running tests ...' -ForegroundColor Cyan
dotnet test IP-Scanner.slnx -nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

Write-Host '[4/4] Publishing single-file exe ...' -ForegroundColor Cyan
dotnet publish src\IpScanner\IpScanner.csproj -r win-x64 --self-contained -c Release `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Write-Host "Done: $root\dist\IP-Scanner.exe" -ForegroundColor Green
