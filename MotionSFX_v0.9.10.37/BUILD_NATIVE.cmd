@echo off
setlocal
cd /d "%~dp0"

set "APP_EXE=%~dp0publish\MotionSFX.exe"
set "MSBUILDTERMINALLOGGER=off"
set "DOTNET_NOLOGO=1"

echo.
echo ============================================================
echo   MotionSFX Alpha 0.9.10.37 - Native Build
echo ============================================================
echo.
echo Build output mode: detailed
echo Successful C# compilation units will be listed as [COMPILED].
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [FAILED] .NET SDK check: dotnet was not found.
    echo Install the .NET 10 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)

echo [1/6] Checking installed .NET SDK...
dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 (
    echo [FAILED] .NET SDK check: .NET 10 SDK is required.
    echo Install the .NET 10 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)
dotnet --version
echo [OK] .NET SDK check complete.
echo.

echo [2/6] Building and running movement-trigger/configuration tests...
echo Cleaning the test project's previous outputs to ensure a fresh, reportable compile...
if exist "Tests\obj" rmdir /s /q "Tests\obj"
if exist "Tests\bin" rmdir /s /q "Tests\bin"
echo [OK] Test outputs cleaned.
echo Test source files will be listed individually after successful compilation.
dotnet run --project "Tests\MovementTriggerStateMachine.Tests.csproj" --configuration Release --no-launch-profile --verbosity normal
if errorlevel 1 (
    echo.
    echo [FAILED] Movement-trigger tests or test-project compilation failed.
    echo The application was not published.
    pause
    exit /b 1
)
echo [OK] All movement-trigger and configuration tests passed.
echo.

echo [3/6] Cleaning previous application build artifacts...
if exist "obj" rmdir /s /q "obj"
if exist "bin" rmdir /s /q "bin"
if exist "publish" rmdir /s /q "publish"
echo [OK] Previous application build artifacts removed.
echo.

echo [4/6] Compiling and publishing MotionSFX for win-x64...
echo Each C# source file successfully included in the compiled application will be listed as [COMPILED].
dotnet publish "MotionSFX.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --output "publish" --verbosity normal --nologo
if errorlevel 1 (
    echo.
    echo [FAILED] MotionSFX compilation or publish failed. Review the compiler output above.
    pause
    exit /b 1
)
echo [OK] MotionSFX compilation and publish completed.
echo.

echo [5/6] Verifying published executable...
if not exist "%APP_EXE%" (
    echo [FAILED] Build reported success, but MotionSFX.exe was not found.
    pause
    exit /b 1
)
for %%F in ("%APP_EXE%") do echo [OK] Executable verified: %%~fF (%%~zF bytes)
echo.

echo [6/6] Desktop shortcut options...
choice /c YN /n /m "Create or update a desktop shortcut? [Y/N] "
if errorlevel 2 goto SkipDesktopShortcut

echo.
echo Creating a desktop shortcut...
echo Cleaning the shortcut utility's previous outputs to ensure a fresh, reportable compile...
if exist "BuildTools\ShortcutCreator\obj" rmdir /s /q "BuildTools\ShortcutCreator\obj"
if exist "BuildTools\ShortcutCreator\bin" rmdir /s /q "BuildTools\ShortcutCreator\bin"
dotnet run --project "BuildTools\ShortcutCreator\ShortcutCreator.csproj" --configuration Release --no-launch-profile --verbosity normal -- "%APP_EXE%"
if errorlevel 1 (
    echo.
    echo [WARNING] MotionSFX was built, but the desktop shortcut could not be created.
    echo You can still run: "%APP_EXE%"
) else (
    echo.
    echo [OK] Desktop shortcut created or updated: MotionSFX.lnk
)
goto DesktopShortcutChoiceComplete

:SkipDesktopShortcut
echo.
echo [OK] Desktop shortcut creation skipped. You can still run: "%APP_EXE%"

:DesktopShortcutChoiceComplete

echo.
echo ============================================================
echo   BUILD COMPLETE - MotionSFX Alpha 0.9.10.37
echo ============================================================
echo Executable: "%APP_EXE%"
echo The OpenVR runtime DLL is bundled for win-x64. Assets and Config are copied into publish automatically.
echo.
echo Starting MotionSFX automatically...
start "" "%APP_EXE%"
echo [OK] MotionSFX launch requested.
echo This window will close automatically.
echo.
for /l %%i in (5,-1,1) do (
    <nul set /p "=Closing in %%i... "
    timeout /t 1 /nobreak >nul
    echo.
)
echo Closing build window...
endlocal
