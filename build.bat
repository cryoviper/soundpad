@echo off
title Build Boobies Soundpad
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK not found. Installing it with winget...
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  echo.
  echo Done. Close this window and run build.bat again.
  pause
  exit /b
)

echo Building... first time takes a couple of minutes.
dotnet publish BoomBx.csproj -c Release -o publish
if errorlevel 1 (
  echo.
  echo Build failed. Copy the red error lines above and send them over.
  pause
  exit /b 1
)

echo.
echo Done! Your app: %~dp0publish\BoobiesSoundpad.exe
explorer "%~dp0publish"
pause
