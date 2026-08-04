@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PUBLISH_OUT=%SCRIPT_DIR%publish"

echo Close Nine Lives (NineLives.exe) if it is running, then press any key to publish...
pause >nul

cd /d "%SCRIPT_DIR%src\NineLives"
dotnet publish -c Release -o "%PUBLISH_OUT%" -r win-x64 --self-contained

set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% equ 0 (
  echo.
  echo Published to: %PUBLISH_OUT%
) else (
  echo.
  echo Publish failed. If you see "file is being used by another process", close the app and run again.
)
exit /b %EXIT_CODE%
