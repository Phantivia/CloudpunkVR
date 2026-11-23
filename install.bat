@echo off
rem Makes the VR copy of Cloudpunk next to your Steam library (see README.md). Extra arguments go to tools\install.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
echo.
pause
