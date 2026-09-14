@echo off
setlocal

set "ROOT=C:\Users\speed\Documents\Bannerlord Events\NativeCharacterImageGenerator"
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
