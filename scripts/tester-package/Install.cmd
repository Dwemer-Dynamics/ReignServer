@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Reign.ps1" %*
set "REIGN_INSTALL_EXIT=%errorlevel%"
if not "%REIGN_INSTALL_EXIT%"=="0" (
  echo.
  echo Installation stopped. Keep this folder and read INSTALL.html before retrying.
)
pause
exit /b %REIGN_INSTALL_EXIT%
