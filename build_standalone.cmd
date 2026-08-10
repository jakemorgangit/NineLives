@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PUBLISH_OUT=%SCRIPT_DIR%publish"

echo Close Nine Lives (NineLives.exe) if it is running, then press any key to publish...
pause >nul

REM Through the publish profile, so this produces the same binary the release workflow does.
REM The flags used to be repeated here and in release.yml, and they had already drifted apart
REM from the profile - see the note in Properties\PublishProfiles\SingleFileExe.pubxml (#39).
cd /d "%SCRIPT_DIR%src\NineLives"
dotnet publish -p:PublishProfile=SingleFileExe -o "%PUBLISH_OUT%"
set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% neq 0 goto :failed

REM The CLI ships beside the app (#63) - same folder, same profile arrangement.
cd /d "%SCRIPT_DIR%src\NineLives.Cli"
dotnet publish -p:PublishProfile=SingleFileExe -o "%PUBLISH_OUT%"
set "EXIT_CODE=%ERRORLEVEL%"
if %EXIT_CODE% neq 0 goto :failed

echo.
echo Published NineLives.exe and 9lives.exe to: %PUBLISH_OUT%
exit /b 0

:failed
echo.
echo Publish failed. If you see "file is being used by another process", close the app and run again.
exit /b %EXIT_CODE%
