@echo off
setlocal EnableExtensions EnableDelayedExpansion
REM One command that runs every check of the project in order and prints a summary table.
REM   test-all.bat          build, unit tests, CLI examples, report schemas, language keys,
REM                         database migration, the release binary
REM   test-all.bat --full   accepted for slower steps; today the whole run fits the 10 minute budget
REM Exit code 0 only when every step passed. Step logs: .tmp\test-all\*.log
cd /d "%~dp0"

set "FULL=0"
if /i "%~1"=="--full" set "FULL=1"
if not "%~1"=="" if not "%FULL%"=="1" ( echo Usage: test-all.bat [--full] & exit /b 2 )

REM English tool output, so the checks below can read it on any Windows language.
set "DOTNET_CLI_UI_LANGUAGE=en"
set "VSLANG=1033"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

set "LOGDIR=%~dp0.tmp\test-all"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
del /q "%LOGDIR%\*.log" >nul 2>&1

set "TESTPROJ=tests\IpScanner.Tests\IpScanner.Tests.csproj"
set "FAILS=0"
set "COUNT=0"
call :clock
set "RUN_START=%NOW%"

REM ---- 1. Build, zero warnings -------------------------------------------------------------
call :begin "1 Build (0 warnings)" build
dotnet build IP-Scanner.slnx --no-incremental "-flp:logfile=%LOGDIR%\build-warnings.log;warningsonly" >"%LOG%" 2>&1
set "RC=!errorlevel!"
if "!RC!"=="0" call :needNoWarnings
call :finish !RC!
set "BUILD_OK=%LAST_OK%"

REM ---- 2. Full unit test suite ---------------------------------------------------------------
call :begin "2 Unit tests (full suite)" suite
if "%BUILD_OK%"=="1" (
    dotnet test IP-Scanner.slnx --no-build --blame-hang-timeout 120s --blame-hang-dump-type none >"%LOG%" 2>&1
    call :finish !errorlevel! counted
) else call :skip

REM ---- 3. CLI examples (README and help examples parse and run) --------------------------------
call :begin "3 CLI examples" cli
if "%BUILD_OK%"=="1" (
    dotnet test %TESTPROJ% --no-build "--filter=FullyQualifiedName~ReadmeCliTests|FullyQualifiedName~CliParserTests.HelpExamples_AllParse" >"%LOG%" 2>&1
    call :finish !errorlevel! counted
) else call :skip

REM ---- 4. Report schemas (XML against the xsd, JSON against its documented fields) ------------
call :begin "4 Report schemas" schema
if "%BUILD_OK%"=="1" (
    dotnet test %TESTPROJ% --no-build "--filter=FullyQualifiedName~ExporterTests|FullyQualifiedName~Ipv6ExportTests" >"%LOG%" 2>&1
    call :finish !errorlevel! counted
) else call :skip

REM ---- 5. Language keys ------------------------------------------------------------------------
call :begin "5 Language keys" lang
if "%BUILD_OK%"=="1" (
    dotnet test %TESTPROJ% --no-build "--filter=FullyQualifiedName~LanguageKeysMatchTests|FullyQualifiedName~LocalizationTests" >"%LOG%" 2>&1
    call :finish !errorlevel! counted
) else call :skip

REM ---- 6. Migration of an old-format database and old config files -----------------------------
call :begin "6 Migration" migration
if "%BUILD_OK%"=="1" (
    dotnet test %TESTPROJ% --no-build "--filter=FullyQualifiedName~MigrationTests" >"%LOG%" 2>&1
    call :finish !errorlevel! counted
) else call :skip

REM ---- 7. Release binaries -----------------------------------------------------------------------
call :begin "7 Release binaries" binaries
echo.>"%LOGDIR%\stamp"
call "%~dp0build.bat" auto >"%LOG%" 2>&1
set "RC=!errorlevel!"
if "!RC!"=="0" call :needBinaries
call :finish !RC!

REM ---- Summary -------------------------------------------------------------------------------------
call :clock
call :elapsed "%RUN_START%" "%NOW%"
echo.
echo ================================================================
echo  test-all summary
echo ================================================================
for /l %%i in (1,1,%COUNT%) do echo  !ROW_%%i!
echo ----------------------------------------------------------------
echo  Total: %ELAPSED_TEXT%
if "%FAILS%"=="0" (
    echo  Result: ALL PASSED
    exit /b 0
)
echo  Result: %FAILS% step^(s^) FAILED, logs in .tmp\test-all
exit /b 1

REM ==================================================================================================

:begin
set /a COUNT+=1
set "STEP_NAME=%~1"
set "LOG=%LOGDIR%\%~2.log"
set "LAST_OK=0"
echo.
echo [!COUNT!] %~1
call :clock
set "STEP_START=%NOW%"
exit /b 0

REM :finish <exit code> [counted]  - records the row, prints the result line of a test step
:finish
set "STEP_OK=0"
if "%~1"=="0" set "STEP_OK=1"
if "%~2"=="counted" (
    findstr /c:"No test matches" /c:"No test is available" "%LOG%" >nul 2>&1 && ( echo     the filter matched no tests & set "STEP_OK=0" )
    setlocal DisableDelayedExpansion
    for /f "tokens=*" %%L in ('findstr /b /c:"Passed" /c:"Failed" "%LOG%" 2^>nul') do echo     %%L
    endlocal
)
call :clock
call :elapsed "%STEP_START%" "%NOW%"
set "STATE=PASS"
if not "%STEP_OK%"=="1" (
    set "STATE=FAIL"
    set /a FAILS+=1
    call :showFailure
)
set "LAST_OK=%STEP_OK%"
set "NAME_PAD=%STEP_NAME%                                   "
set "ROW_%COUNT%=!NAME_PAD:~0,36! !STATE!   %ELAPSED_TEXT%"
echo     !STATE!  %ELAPSED_TEXT%
exit /b 0

:skip
set /a FAILS+=1
set "LAST_OK=0"
set "NAME_PAD=%STEP_NAME%                                   "
set "ROW_%COUNT%=!NAME_PAD:~0,36! SKIP   build failed"
echo     SKIP  build failed
exit /b 0

REM Shows the lines of the log that name the failure.
:showFailure
set "SHOWN=0"
for /f "tokens=*" %%L in ('findstr /i /c:"[FAIL]" /c:"error " /c:"warning " /c:"Failed " /c:"Assert" /c:"Error Message" "%LOG%" 2^>nul') do (
    if !SHOWN! lss 12 ( echo     %%L & set /a SHOWN+=1 )
)
exit /b 0

REM A warning-free build leaves a warnings log without any "warning" line.
:needNoWarnings
if not exist "%LOGDIR%\build-warnings.log" exit /b 0
findstr /c:"warning" "%LOGDIR%\build-warnings.log" >nul 2>&1 && (
    echo     the build printed warnings, see .tmp\test-all\build-warnings.log
    findstr /c:"warning" "%LOGDIR%\build-warnings.log"
    set "RC=1"
)
exit /b 0

REM The exe must be newer than the step start and IP-Scanner.exe --help must exit 0 (start /wait: cmd does not wait for a window exe otherwise).
:needBinaries
for %%E in (IP-Scanner.exe) do (
    if not exist "dist\%%E" ( echo     dist\%%E is missing & set "RC=1" ) else (
        powershell -NoProfile -Command "if ((Get-Item 'dist\%%E').LastWriteTimeUtc -gt (Get-Item '%LOGDIR%\stamp').LastWriteTimeUtc) { exit 0 } else { exit 1 }"
        if errorlevel 1 ( echo     dist\%%E was not rebuilt & set "RC=1" )
    )
)
if "!RC!"=="0" (
    start "" /wait "dist\IP-Scanner.exe" --help >>"%LOG%" 2>&1
    if errorlevel 1 ( echo     IP-Scanner.exe --help failed & set "RC=1" )
)
exit /b 0

REM :clock  - sets NOW to the time of day in hundredths of a second
:clock
set "T=%TIME: =0%"
set /a NOW=((1%T:~0,2%-100)*3600+(1%T:~3,2%-100)*60+(1%T:~6,2%-100))*100+(1%T:~9,2%-100)
exit /b 0

REM :elapsed <start> <end>  - sets ELAPSED_TEXT as m:ss.s
:elapsed
set /a D=%~2-%~1
if !D! lss 0 set /a D+=8640000
set /a SEC=D/100, M=SEC/60, S=SEC%%60, TENTH=(D%%100)/10
set "SS=0!S!"
set "ELAPSED_TEXT=!M!:!SS:~-2!.!TENTH!"
exit /b 0
