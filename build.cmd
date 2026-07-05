@echo off
REM Build KmTronicRelay via WSL (cross-publish to Windows x64).
cd /d "%~dp0"
wsl.exe bash -lc "./build.sh"
exit /b %ERRORLEVEL%
