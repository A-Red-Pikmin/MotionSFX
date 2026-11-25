@echo off
setlocal EnableExtensions
rem Support running from the application root before the first build or from BuildTools afterward.
for %%I in ("%~dp0.") do set "APP_DIR=%%~fI"
if not exist "%APP_DIR%\Source\MotionSFX.csproj" for %%I in ("%~dp0..") do set "APP_DIR=%%~fI"
if "%APP_DIR:~-1%"=="\" set "APP_DIR=%APP_DIR:~0,-1%"
set "BUILD_TOOLS_DIR=%APP_DIR%\BuildTools"
set "SOURCE_DIR=%APP_DIR%\Source"
set "APP_EXE=%APP_DIR%\MotionSFX.exe"
set "PUBLISH_STAGING=%APP_DIR%\_publish_staging"
set "DOTNET_CMD=dotnet"
set "MSBUILDTERMINALLOGGER=off"
set "DOTNET_NOLOGO=1"
cd /d "%APP_DIR%"

rem Enable per-line ANSI colors in current Windows consoles; leave colors blank if unsupported.
for /F "delims=" %%A in ('echo prompt $E^| cmd') do set "ESC=%%A"
set "C_RESET="
set "C_BANNER="
set "C_PHASE="
set "C_INFO="
set "C_OK="
set "C_WARN="
set "C_ERROR="
set "C_PROMPT="
set "C_COMPILED="
if defined ESC (
    set "C_RESET=%ESC%[0m"
    set "C_BANNER=%ESC%[95m"
    set "C_PHASE=%ESC%[96m"
    set "C_INFO=%ESC%[90m"
    set "C_OK=%ESC%[92m"
    set "C_WARN=%ESC%[93m"
    set "C_ERROR=%ESC%[91m"
    set "C_PROMPT=%ESC%[95m"
    set "C_COMPILED=%ESC%[96m"
)
rem Pass colors to MSBuild so its per-file [COMPILED] reports match the build log.
set "MOTIONSFX_ANSI_CYAN=%C_COMPILED%"
set "MOTIONSFX_ANSI_RESET=%C_RESET%"

echo.
echo %C_BANNER%============================================================%C_RESET%
echo %C_BANNER%  MotionSFX Alpha 0.9.10.55 - Native Build%C_RESET%
echo %C_BANNER%============================================================%C_RESET%
echo.

call :StartTask "SDK preflight"
echo %C_PHASE%[1/7] Checking installed .NET SDK before any build or test work...%C_RESET%
set "HAS_DOTNET10="
where dotnet >nul 2>nul
if not errorlevel 1 (
    for /f "tokens=1" %%V in ('dotnet --list-sdks 2^>nul ^| findstr /b "10."') do set "HAS_DOTNET10=1"
)

if not defined HAS_DOTNET10 (
    echo %C_WARN%[NOTICE] .NET 10 SDK was not detected. Attempting automatic installation with winget...%C_RESET%
    where winget >nul 2>nul
    if errorlevel 1 (
        echo %C_ERROR%[FAILED] winget was not found, so the .NET 10 SDK could not be installed automatically.%C_RESET%
        call :EndTask "FAILED"
        echo %C_INFO%Install the .NET 10 SDK manually from:%C_RESET%
        echo https://dotnet.microsoft.com/download/dotnet/10.0
        echo.
        pause
        exit /b 1
    ) else (
        echo %C_INFO%Running: winget install Microsoft.DotNet.SDK.10%C_RESET%
        winget install Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements
        if errorlevel 1 (
            echo %C_ERROR%[FAILED] winget could not install Microsoft.DotNet.SDK.10.%C_RESET%
            call :EndTask "FAILED"
            echo %C_INFO%Check the winget output above, install the SDK manually if needed, and run this build again.%C_RESET%
            echo.
            pause
            exit /b 1
        )
    )
)

rem Refresh the standard machine install path because winget may update PATH only for new terminals.
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_CMD=%ProgramFiles%\dotnet\dotnet.exe"
"%DOTNET_CMD%" --list-sdks | findstr /b "10." >nul
if errorlevel 1 (
    echo %C_ERROR%[FAILED] .NET 10 SDK is still unavailable after the installation check.%C_RESET%
    call :EndTask "FAILED"
    echo %C_INFO%Restart Windows or install the .NET 10 SDK manually, then run this build again.%C_RESET%
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)
"%DOTNET_CMD%" --version
echo %C_OK%[OK] .NET 10 SDK check complete.%C_RESET%
call :EndTask "COMPLETED"
echo.

if not exist "%SOURCE_DIR%\MotionSFX.csproj" (
    echo %C_ERROR%[FAILED] The organized Source\MotionSFX.csproj project was not found.%C_RESET%
    echo %C_INFO%Expected project: "%SOURCE_DIR%\MotionSFX.csproj"%C_RESET%
    pause
    exit /b 1
)

call :StartTask "Movement-trigger and configuration tests"
echo %C_PHASE%[2/7] Building and running movement-trigger/configuration tests...%C_RESET%
echo %C_INFO%Cleaning the test project's previous outputs to ensure a fresh, reportable compile...
if exist "%SOURCE_DIR%\Tests\obj" rmdir /s /q "%SOURCE_DIR%\Tests\obj"
if exist "%SOURCE_DIR%\Tests\bin" rmdir /s /q "%SOURCE_DIR%\Tests\bin"
echo %C_OK%[OK] Test outputs cleaned.%C_RESET%
echo %C_INFO%Test source files will be listed individually after successful compilation.%C_RESET%
"%DOTNET_CMD%" run --project "%SOURCE_DIR%\Tests\MovementTriggerStateMachine.Tests.csproj" --configuration Release --no-launch-profile --verbosity minimal
if errorlevel 1 (
    echo.
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] Movement-trigger tests or test-project compilation failed.%C_RESET%
    echo %C_ERROR%The application was not published.%C_RESET%
    call :CleanupBuildArtifacts
    pause
    exit /b 1
)
echo %C_OK%[OK] All movement-trigger and configuration tests passed.%C_RESET%
call :EndTask "COMPLETED"
echo.

call :StartTask "Publish area preparation"
echo %C_PHASE%[3/7] Preparing a clean temporary publish area...%C_RESET%
call :CleanupBuildArtifacts
if exist "%PUBLISH_STAGING%" rmdir /s /q "%PUBLISH_STAGING%"
if exist "%APP_DIR%\publish" rmdir /s /q "%APP_DIR%\publish"
echo %C_OK%[OK] Previous build intermediates and legacy publish folder removed.%C_RESET%
call :EndTask "COMPLETED"
echo.

call :StartTask "Application compile and publish"
echo %C_PHASE%[4/7] Compiling and publishing MotionSFX for win-x64...%C_RESET%
echo %C_INFO%Published runtime files will be staged temporarily, then copied into the organized application root.%C_RESET%
echo %C_INFO%C# compilation units are listed individually as [COMPILED].%C_RESET%
"%DOTNET_CMD%" publish "%SOURCE_DIR%\MotionSFX.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --output "%PUBLISH_STAGING%" --verbosity minimal --nologo
if errorlevel 1 (
    echo.
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] MotionSFX compilation or publish failed. Review the compiler output above.%C_RESET%
    call :CleanupBuildArtifacts
    pause
    exit /b 1
)
echo %C_OK%[OK] MotionSFX compilation and publish completed.%C_RESET%
call :EndTask "COMPLETED"
echo.

call :StartTask "Install published application"
echo %C_PHASE%[5/7] Installing the published application into the main directory...%C_RESET%
if not exist "%PUBLISH_STAGING%\MotionSFX.exe" (
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] The temporary publish output does not contain MotionSFX.exe.%C_RESET%
    call :CleanupBuildArtifacts
    pause
    exit /b 1
)
robocopy "%PUBLISH_STAGING%" "%APP_DIR%" /E /COPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP
set "COPY_EXIT=%ERRORLEVEL%"
if %COPY_EXIT% GEQ 8 (
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] Could not copy the published application into the main directory. Robocopy exit code: %COPY_EXIT%%C_RESET%
    echo %C_WARN%Temporary publish files have been retained for diagnosis.%C_RESET%
    pause
    exit /b 1
)
if not exist "%APP_EXE%" (
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] Build reported success, but MotionSFX.exe was not found in the main directory.%C_RESET%
    call :CleanupBuildArtifacts
    pause
    exit /b 1
)
for %%F in ("%APP_EXE%") do echo %C_OK%[OK] Executable verified: %%~fF (%%~zF bytes)%C_RESET%
call :EndTask "COMPLETED"
echo.

call :StartTask "Desktop shortcut setup"
set "SHORTCUT_RESULT=COMPLETED"
echo %C_PHASE%[6/7] Desktop shortcut options...%C_RESET%
choice /c YN /n /m "%C_PROMPT%Create or update a desktop shortcut? [Y/N] %C_RESET%"
if errorlevel 2 goto SkipDesktopShortcut

echo.
echo %C_INFO%Creating a desktop shortcut...%C_RESET%
echo Cleaning the shortcut utility's previous outputs to ensure a fresh, reportable compile...
if exist "%BUILD_TOOLS_DIR%\ShortcutCreator\obj" rmdir /s /q "%BUILD_TOOLS_DIR%\ShortcutCreator\obj"
if exist "%BUILD_TOOLS_DIR%\ShortcutCreator\bin" rmdir /s /q "%BUILD_TOOLS_DIR%\ShortcutCreator\bin"
"%DOTNET_CMD%" run --project "%BUILD_TOOLS_DIR%\ShortcutCreator\ShortcutCreator.csproj" --configuration Release --no-launch-profile --verbosity minimal -- "%APP_EXE%"
if errorlevel 1 (
    set "SHORTCUT_RESULT=WARNING"
    echo.
    echo %C_WARN%[WARNING] MotionSFX was built, but the desktop shortcut could not be created.%C_RESET%
    echo You can still run: "%APP_EXE%"
) else (
    echo.
    echo %C_OK%[OK] Desktop shortcut created or updated: MotionSFX.lnk%C_RESET%
)
goto DesktopShortcutChoiceComplete

:SkipDesktopShortcut
set "SHORTCUT_RESULT=SKIPPED"
echo.
echo %C_OK%[OK] Desktop shortcut creation skipped.%C_RESET%

:DesktopShortcutChoiceComplete
call :EndTask "%SHORTCUT_RESULT%"

echo.
call :StartTask "Cleanup and file organization"
echo %C_PHASE%[7/7] Removing temporary build files and verifying the organized application folders...%C_RESET%
call :CleanupBuildArtifacts
if exist "%PUBLISH_STAGING%" rmdir /s /q "%PUBLISH_STAGING%"
if exist "%APP_DIR%\publish" rmdir /s /q "%APP_DIR%\publish"
call :OrganizeLooseFiles
if not exist "%APP_EXE%" (
    call :EndTask "FAILED"
    echo %C_ERROR%[FAILED] MotionSFX.exe is missing from the application root after organization.%C_RESET%
    pause
    exit /b 1
)
echo %C_OK%Main folder contains MotionSFX.exe plus categorized folders: Assets, Config, Source, BuildTools, and Documentation.%C_RESET%
echo.
echo %C_PHASE%Starting MotionSFX automatically...%C_RESET%
start "" /D "%APP_DIR%" "%APP_EXE%"
if errorlevel 1 echo %C_WARN%[WARNING] MotionSFX was built, but Windows could not start it automatically.%C_RESET%
call :EndTask "COMPLETED"
echo.
echo %C_OK%[COMPLETE] MotionSFX build and organization finished.%C_RESET%
echo Executable: "%APP_EXE%"
exit /b 0

:StartTask
set "TASK_LABEL=%~1"
set "TASK_START_TIME=%TIME: =0%"
set /a "TASK_START_CS=(1%TASK_START_TIME:~0,2%-100)*360000+(1%TASK_START_TIME:~3,2%-100)*6000+(1%TASK_START_TIME:~6,2%-100)*100+(1%TASK_START_TIME:~9,2%-100)"
exit /b 0

:EndTask
set "TASK_RESULT=%~1"
if not defined TASK_RESULT set "TASK_RESULT=COMPLETED"
set "TASK_END_TIME=%TIME: =0%"
set /a "TASK_END_CS=(1%TASK_END_TIME:~0,2%-100)*360000+(1%TASK_END_TIME:~3,2%-100)*6000+(1%TASK_END_TIME:~6,2%-100)*100+(1%TASK_END_TIME:~9,2%-100)"
set /a "TASK_ELAPSED_CS=TASK_END_CS-TASK_START_CS"
if %TASK_ELAPSED_CS% LSS 0 set /a TASK_ELAPSED_CS+=8640000
set /a "TASK_HOURS=TASK_ELAPSED_CS/360000"
set /a "TASK_REMAINING_CS=TASK_ELAPSED_CS-TASK_HOURS*360000"
set /a "TASK_MINUTES=TASK_REMAINING_CS/6000"
set /a "TASK_REMAINING_CS=TASK_REMAINING_CS-TASK_MINUTES*6000"
set /a "TASK_SECONDS=TASK_REMAINING_CS/100"
set /a "TASK_CENTISECONDS=TASK_REMAINING_CS-TASK_SECONDS*100"
if %TASK_HOURS% LSS 10 set "TASK_HOURS=0%TASK_HOURS%"
if %TASK_MINUTES% LSS 10 set "TASK_MINUTES=0%TASK_MINUTES%"
if %TASK_SECONDS% LSS 10 set "TASK_SECONDS=0%TASK_SECONDS%"
if %TASK_CENTISECONDS% LSS 10 set "TASK_CENTISECONDS=0%TASK_CENTISECONDS%"
echo %C_INFO%[TIME] %TASK_LABEL% - %TASK_RESULT% in %TASK_HOURS%:%TASK_MINUTES%:%TASK_SECONDS%.%TASK_CENTISECONDS%%C_RESET%
exit /b 0

:OrganizeLooseFiles
rem Sort loose application files into their matching folders. Keep MotionSFX.exe in the root.
if not exist "%APP_DIR%\Documentation\" mkdir "%APP_DIR%\Documentation"
if not exist "%APP_DIR%\Source\" mkdir "%APP_DIR%\Source"
if not exist "%APP_DIR%\BuildTools\" mkdir "%APP_DIR%\BuildTools"
set "DOCUMENTATION_DIR=%APP_DIR%\Documentation\"
set "SOURCE_FILES_DIR=%APP_DIR%\Source\"
set "BUILD_TOOLS_DIR=%APP_DIR%\BuildTools"
set "BUILD_UTILITIES_DIR=%BUILD_TOOLS_DIR%\Utilities"

for %%F in ("%APP_DIR%\*.txt" "%APP_DIR%\*.md" "%APP_DIR%\*.log" "%APP_DIR%\*.csv" "%APP_DIR%\*.xml" "%APP_DIR%\*.ini" "%APP_DIR%\*.yml" "%APP_DIR%\*.yaml") do (
    if exist "%%~fF" (
        move /Y "%%~fF" "%DOCUMENTATION_DIR%" >nul
        if errorlevel 1 echo %C_WARN%[WARNING] Could not move "%%~nxF" into Documentation.%C_RESET%
    )
)
for %%F in ("%APP_DIR%\*.cs" "%APP_DIR%\*.xaml" "%APP_DIR%\*.csproj" "%APP_DIR%\*.sln" "%APP_DIR%\*.props" "%APP_DIR%\*.targets") do (
    if exist "%%~fF" (
        move /Y "%%~fF" "%SOURCE_FILES_DIR%" >nul
        if errorlevel 1 echo %C_WARN%[WARNING] Could not move "%%~nxF" into Source.%C_RESET%
    )
)
for %%F in ("%APP_DIR%\*.cmd" "%APP_DIR%\*.bat" "%APP_DIR%\*.ps1" "%APP_DIR%\*.sh") do (
    if exist "%%~fF" if /I not "%%~nxF"=="BUILD_NATIVE.cmd" (
        move /Y "%%~fF" "%BUILD_TOOLS_DIR%" >nul
        if errorlevel 1 echo %C_WARN%[WARNING] Could not move "%%~nxF" into BuildTools.%C_RESET%
    )
)
for %%F in ("%APP_DIR%\*.exe" "%APP_DIR%\*.msi" "%APP_DIR%\*.msix") do (
    if exist "%%~fF" if /I not "%%~nxF"=="MotionSFX.exe" (
        if not exist "%BUILD_UTILITIES_DIR%" mkdir "%BUILD_UTILITIES_DIR%"
        move /Y "%%~fF" "%BUILD_UTILITIES_DIR%" >nul
        if errorlevel 1 echo %C_WARN%[WARNING] Could not move auxiliary program "%%~nxF" into BuildTools\Utilities.%C_RESET%
    )
)
rem Move the running build script only after compilation, cleanup, and all other organization is complete.
if exist "%APP_DIR%\BUILD_NATIVE.cmd" (
    move /Y "%APP_DIR%\BUILD_NATIVE.cmd" "%BUILD_TOOLS_DIR%\BUILD_NATIVE.cmd" >nul
    if errorlevel 1 (
        echo %C_WARN%[WARNING] Build completed, but BUILD_NATIVE.cmd could not be moved into BuildTools.%C_RESET%
    ) else (
        echo %C_OK%[OK] BUILD_NATIVE.cmd moved into BuildTools after the build completed.%C_RESET%
    )
)
exit /b 0

:CleanupBuildArtifacts
if exist "%SOURCE_DIR%\obj" rmdir /s /q "%SOURCE_DIR%\obj"
if exist "%SOURCE_DIR%\bin" rmdir /s /q "%SOURCE_DIR%\bin"
if exist "%SOURCE_DIR%\Tests\obj" rmdir /s /q "%SOURCE_DIR%\Tests\obj"
if exist "%SOURCE_DIR%\Tests\bin" rmdir /s /q "%SOURCE_DIR%\Tests\bin"
if exist "%BUILD_TOOLS_DIR%\ShortcutCreator\obj" rmdir /s /q "%BUILD_TOOLS_DIR%\ShortcutCreator\obj"
if exist "%BUILD_TOOLS_DIR%\ShortcutCreator\bin" rmdir /s /q "%BUILD_TOOLS_DIR%\ShortcutCreator\bin"
if exist "%PUBLISH_STAGING%" rmdir /s /q "%PUBLISH_STAGING%"
if exist "%APP_DIR%\publish" rmdir /s /q "%APP_DIR%\publish"
exit /b 0
