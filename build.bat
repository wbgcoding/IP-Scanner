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
set BUILD_RC=%errorlevel%
if not "%BUILD_RC%"=="0" goto :failed
copy /y THIRD-PARTY-NOTICES.md dist\ >nul
REM The separate console exe of earlier versions is gone; a leftover one would be stale.
del "dist\ipscan-cli.exe" >nul 2>&1
REM Update signature: only for a manual build, and only when the signing key exists
REM on this machine. Signature files left from an earlier build would no longer
REM match the new exe, so they go first.
del "dist\IP-Scanner.exe.sig" "dist\IP-Scanner.exe.sha256" >nul 2>&1
if /i "%~1"=="auto" ( echo Automatic build: dist\IP-Scanner.exe stays unsigned. & goto :signed )
if not exist "%APPDATA%\IP-Scanner-signing\key.bin" ( echo No signing key on this machine: dist\IP-Scanner.exe stays unsigned. & goto :signed )
dotnet run --project scripts\ReleaseSigner -- sign dist\IP-Scanner.exe
if errorlevel 1 ( echo SIGNING FAILED: dist\IP-Scanner.exe is not signed. & set BUILD_RC=1 )
:signed
REM "auto" (passed by the session-end autobuild hook) skips the pause below --
REM nobody is there to read it, so it just delays the next turn for nothing.
if /i "%~1"=="auto" goto :done
if not "%BUILD_RC%"=="0" ( echo BUILD FAILED & timeout /t 30 & exit /b 1 )
echo.
echo Done: %~dp0dist\IP-Scanner.exe
timeout /t 30
goto :eof
:failed
if /i "%~1"=="auto" exit /b 1
echo BUILD FAILED & timeout /t 30 & exit /b 1
:done
if not "%BUILD_RC%"=="0" exit /b 1
echo.
echo Done: %~dp0dist\IP-Scanner.exe
endlocal
