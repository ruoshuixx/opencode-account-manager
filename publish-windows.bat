@echo off
rem 打包成单个独立 exe（自带 .NET 运行时，无需再装）
rem 产物在 dist\win-x64\OpenCodeAccountManager.exe
setlocal
cd /d "%~dp0"
dotnet publish src\AccountManager.App\AccountManager.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\win-x64
echo.
echo 完成： dist\win-x64\OpenCodeAccountManager.exe
endlocal
