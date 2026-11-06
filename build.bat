@echo off
title IP-Scanner
setlocal
cd /d "%~dp0"
rem double-click run (cmd /c, NOPAUSE unset, not "auto"): small window, stays open on error, closes 10 s after success
set "DBL="
if /i not "%~1"=="auto" if not defined NOPAUSE echo %CMDCMDLINE% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "DBL=1"
if defined DBL mode con cols=100 lines=25
rem === build freshness: check ===
set "BS=%~dp0tools\build-stamp.ps1"
set "BSKEY=exe"
set "BSEXTRA="
set "BSART=dist\IP-Scanner.exe"
if /i "%~1"=="auto" set "BSEXTRA=auto"
set "BSFRESH="
set "BSFORCE="
if "%FORCE%"=="1" set "BSFORCE=1"
for %%A in (%*) do if /i "%%~A"=="--force" set "BSFORCE=1"
if defined BSFORCE goto :bs_start
if not exist "%BS%" goto :bs_start
for /f "usebackq delims=" %%A in (`powershell -NoProfile -ExecutionPolicy RemoteSigned -File "%BS%" -Check -Project "%~dp0." -Key "%BSKEY%" -Extra "%BSEXTRA%"`) do set "BSFRESH=%%A"
if defined BSFRESH goto :bs_uptodate
:bs_start
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
rem === build freshness: save (the exe plus the signature files when the signer wrote them) ===
if not "%BUILD_RC%"=="0" goto :bs_nosave
set "BSART=dist\IP-Scanner.exe"
if exist "dist\IP-Scanner.exe.sig" set "BSART=%BSART%,dist\IP-Scanner.exe.sig"
if exist "dist\IP-Scanner.exe.sha256" set "BSART=%BSART%,dist\IP-Scanner.exe.sha256"
if exist "%BS%" powershell -NoProfile -ExecutionPolicy RemoteSigned -File "%BS%" -Save -Project "%~dp0." -Key "%BSKEY%" -Extra "%BSEXTRA%" -Artifact "%BSART%" >nul 2>&1
:bs_nosave
REM "auto" (passed by the session-end autobuild hook) skips the pause below --
REM nobody is there to read it, so it just delays the next turn for nothing.
if /i "%~1"=="auto" goto :done
if not "%BUILD_RC%"=="0" goto :failed
echo.
echo Done: %~dp0dist\IP-Scanner.exe
if defined DBL "%SystemRoot%\System32\timeout.exe" /t 10
exit /b 0
:failed
if /i "%~1"=="auto" exit /b 1
echo BUILD FAILED
if defined DBL pause
exit /b 1
:done
if not "%BUILD_RC%"=="0" exit /b 1
echo.
echo Done: %~dp0dist\IP-Scanner.exe
endlocal
exit /b 0

:bs_uptodate
for %%F in ("%BSFRESH%") do echo Build is up to date (last build %%~tF), nothing to do: %%~fF
if defined DBL "%SystemRoot%\System32\timeout.exe" /t 10
exit /b 0
