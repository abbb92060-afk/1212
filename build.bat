@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK not found.
  echo Install .NET 8 SDK from Microsoft, then run this file again.
  pause
  exit /b 1
)

echo Restoring packages...
dotnet restore

if errorlevel 1 (
  echo Restore failed.
  pause
  exit /b 1
)

echo Building Windows x64 single-file application...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o dist

echo.
echo Output:
echo dist\TRON-Auto-Sweeper.exe
pause
