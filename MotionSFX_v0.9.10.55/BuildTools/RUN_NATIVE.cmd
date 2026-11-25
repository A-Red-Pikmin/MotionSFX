@echo off
setlocal EnableExtensions
for %%I in ("%~dp0..") do set "APP_DIR=%%~fI"
if "%APP_DIR:~-1%"=="\" set "APP_DIR=%APP_DIR:~0,-1%"
set "APP_EXE=%APP_DIR%\MotionSFX.exe"
if not exist "%APP_EXE%" (
    echo MotionSFX.exe has not been built yet. Run BuildTools\BUILD_NATIVE.cmd first.
    pause
    exit /b 1
)
start "" /D "%APP_DIR%" "%APP_EXE%"
endlocal
