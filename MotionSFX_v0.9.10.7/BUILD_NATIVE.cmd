@echo off
setlocal
cd /d "%~dp0"

set "APP_EXE=%~dp0publish\MotionSFX.exe"

echo.
echo ============================================
echo   MotionSFX Alpha 0.9.10.7 - Native Build
echo ============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: .NET 10 SDK was not found.
    echo Install the .NET 10 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo Then run BUILD_NATIVE.cmd again.
    echo.
    pause
    exit /b 1
)

dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 (
    echo ERROR: The .NET 10 SDK is required to build this version.
    echo Install the .NET 10 SDK from:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    pause
    exit /b 1
)

if exist "publish" rmdir /s /q "publish"

dotnet publish "MotionSFX.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "publish"
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Review the compiler output above.
    pause
    exit /b 1
)

if not exist "%APP_EXE%" (
    echo.
    echo ERROR: Build reported success, but MotionSFX.exe was not found.
    pause
    exit /b 1
)

echo.
echo Creating a desktop shortcut...
dotnet run --project "BuildTools\ShortcutCreator\ShortcutCreator.csproj" --configuration Release --no-launch-profile -- "%APP_EXE%"
if errorlevel 1 (
    echo.
    echo WARNING: MotionSFX was built, but the desktop shortcut could not be created.
    echo You can still run: "%APP_EXE%"
) else (
    echo.
    echo Desktop shortcut created: MotionSFX.lnk
)

echo.
echo BUILD COMPLETE.
echo Executable: "%APP_EXE%"
echo The OpenVR runtime DLL is bundled for win-x64. Assets and Config are copied into publish automatically.
echo.
echo Starting MotionSFX automatically...
start "" "%APP_EXE%"
echo MotionSFX has been launched.
echo This window will close automatically.
echo.
for /l %%i in (5,-1,1) do (
    <nul set /p "=Closing in %%i... "
    timeout /t 1 /nobreak >nul
    echo.
)
echo Closing build window...
endlocal
