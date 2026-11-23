@echo off
setlocal

set "ROOT=%~dp0.."
set "STUDIO=%ROOT%\publish\app\Bannerlord.NativeCharacterImageGenerator.App.exe"

if not exist "%STUDIO%" (
    echo Bannerlord Native Character Studio was not found:
    echo %STUDIO%
    echo.
    pause
    exit /b 1
)

start "" "%STUDIO%"
endlocal
