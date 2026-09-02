@echo off
setlocal
cd /d "%~dp0"

taskkill /F /IM QSurfer.exe >nul 2>&1
ping 127.0.0.1 -n 2 >nul

if exist dist\QSurfer rmdir /S /Q dist\QSurfer

dotnet publish src\QSurfer.Avalonia\QSurfer.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\QSurfer
if errorlevel 1 exit /b %errorlevel%

mkdir dist\QSurfer\config >nul 2>&1
copy /Y config.template.json dist\QSurfer\config\config.json >nul
echo.
echo QSurfer package created at dist\QSurfer
