@echo off
:: ══════════════════════════════════════════════════════════════════
::  deploy-bundle.bat  –  Quick deploy for development/testing
:: ══════════════════════════════════════════════════════════════════
::
::  Copies the built .bundle folder to the Autodesk ApplicationPlugins
::  directory so Civil 3D auto-loads it on next startup.
::
::  Usage:  Run after building in Visual Studio (Release x64)
::          Close Civil 3D first!
:: ══════════════════════════════════════════════════════════════════

setlocal

set BUNDLE_NAME=AdvancedLandDevTools.bundle
set SOURCE=%~dp0Publish\%BUNDLE_NAME%
set TARGET=%APPDATA%\Autodesk\ApplicationPlugins\%BUNDLE_NAME%

echo.
echo  Advanced Land Development Tools – Bundle Deploy
echo  ================================================
echo.

:: Check source exists
if not exist "%SOURCE%\PackageContents.xml" (
    echo  ERROR: Bundle not found at:
    echo    %SOURCE%
    echo.
    echo  Build the project first:
    echo    dotnet build -c Release -p:Platform=x64
    echo.
    pause
    exit /b 1
)

:: Check if Civil 3D is running
tasklist /FI "IMAGENAME eq acad.exe" 2>NUL | find /I /N "acad.exe" >NUL
if "%ERRORLEVEL%"=="0" (
    echo  WARNING: Civil 3D appears to be running.
    echo  Close Civil 3D before deploying, or the DLL will be locked.
    echo.
    choice /C YN /M "  Continue anyway?"
    if errorlevel 2 exit /b 0
)

:: Create target directory
if not exist "%APPDATA%\Autodesk\ApplicationPlugins" (
    mkdir "%APPDATA%\Autodesk\ApplicationPlugins"
)

:: Copy bundle
echo  Copying bundle...
xcopy /E /I /Y "%SOURCE%" "%TARGET%" >NUL

echo.
echo  ✓ Deployed to:
echo    %TARGET%
echo.
echo  Start Civil 3D 2026 — the ribbon tab will appear automatically.
echo.

pause
