@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" goto usage
if not defined VOLTRA_SIGNING_KEY goto usage
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Build-Installer.ps1"
if errorlevel 1 exit /b 1
for /f "usebackq delims=" %%V in (`powershell.exe -NoProfile -Command "([xml](Get-Content -Raw 'Directory.Build.props')).Project.PropertyGroup.VoltraVersion | Where-Object { $_ }"`) do set "VERSION=%%V"
py -3 release-tools\prepare.py "installer\Output\Voltra-%VERSION%-win-x64.exe" "%~1" --key "%VOLTRA_SIGNING_KEY%"
exit /b %errorlevel%
:usage
echo Set VOLTRA_SIGNING_KEY to the existing private key outside the repository.
echo Usage: release.bat PATH_TO_VERSIONED_LINUX_ARCHIVE
exit /b 2
