@echo off
setlocal
cd /d "%~dp0"
dotnet run --project src\AccountManager.App -c Release
endlocal
