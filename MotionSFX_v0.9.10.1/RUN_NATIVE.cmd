@echo off
setlocal
if not exist "%~dp0publish\MotionSFX.exe" (
    echo MotionSFX.exe has not been built yet. Run BUILD_NATIVE.cmd first.
    pause
    exit /b 1
)
start "" "%~dp0publish\MotionSFX.exe"
endlocal
