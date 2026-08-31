@echo off
setlocal
cd /d "%~dp0"

dotnet publish src\QSurfer.Avalonia\QSurfer.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\QSurfer
if errorlevel 1 exit /b %errorlevel%

if not exist dist\QSurfer\config.json copy /Y config.template.json dist\QSurfer\config.json >nul
echo.
echo QSurfer package created at dist\QSurfer
