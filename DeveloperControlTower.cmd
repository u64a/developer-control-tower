@echo off
setlocal

rem Developer Control Tower launcher.
rem Looks for the installed app at:
rem   1) The path passed as the first argument, if any.
rem   2) %ProgramFiles%\Development Tower (the canonical default).
rem If the install is missing, points at Install-DeveloperControlTower.ps1
rem so first-time setup is one command.

set "INSTALL_DIR=%~1"
if "%INSTALL_DIR%"=="" set "INSTALL_DIR=%ProgramFiles%\Development Tower"
set "EXE=%INSTALL_DIR%\ControlTower.Desktop.exe"

if not exist "%EXE%" (
  echo Developer Control Tower is not installed at:
  echo   %EXE%
  echo.
  echo Run Install-DeveloperControlTower.ps1 first, e.g.:
  echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DeveloperControlTower.ps1"
  echo.
  echo Or pass a custom install directory as the first argument:
  echo   %~nx0 "C:\Tools\DevTower"
  exit /b 1
)

start "" "%EXE%"
endlocal
