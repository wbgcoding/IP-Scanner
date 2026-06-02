@echo off
setlocal
cd /d "%~dp0"
echo Building IP-Scanner single-file exe ...
dotnet publish src\IpScanner\IpScanner.csproj ^
    -r win-x64 --self-contained -c Release ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none -p:DebugSymbols=false ^
    -o dist
if errorlevel 1 ( echo BUILD FAILED & pause & exit /b 1 )
echo.
echo Done: %~dp0dist\IP-Scanner.exe
pause
endlocal
