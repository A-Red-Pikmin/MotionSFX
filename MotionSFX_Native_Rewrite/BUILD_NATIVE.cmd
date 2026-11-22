@echo off
setlocal
cd /d "%~dp0"

echo.
echo ============================================
echo   MotionSFX Alpha 0.9.9.0 - Native Build
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

echo.
echo BUILD COMPLETE.
echo Executable: "%~dp0publish\MotionSFX.exe"
echo Keep the Assets and Config folders next to the executable; the build copies them into publish automatically.
echo.
pause
endlocal
