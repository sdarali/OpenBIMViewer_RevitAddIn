@echo off
rem Installs (or removes) the staged add-in for the current Windows user:
rem   build\dist\<year>\ -> %APPDATA%\Autodesk\Revit\Addins\<year>\.
rem Command-console counterpart of install.ps1.
rem
rem   install.cmd                    every built version whose Revit is installed
rem   install.cmd 2026               just Revit 2026 (installed or not)
rem   install.cmd 2024 2027
rem   install.cmd /uninstall         remove from every built version whose Revit is installed
rem   install.cmd 2026 /uninstall
setlocal EnableExtensions EnableDelayedExpansion

set "DIST=%~dp0build\dist"
set "VERSIONS="
set "UNINSTALL="

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/uninstall" (set "UNINSTALL=1" & shift & goto parse)
if /i "%~1"=="-uninstall" (set "UNINSTALL=1" & shift & goto parse)
if /i "%~1"=="--uninstall" (set "UNINSTALL=1" & shift & goto parse)
if "%~1"=="/?" goto usage
if /i "%~1"=="-h" goto usage
if /i "%~1"=="--help" goto usage
echo %~1| findstr /r "^[0-9][0-9][0-9][0-9]$" >nul || (
    echo Unknown argument: %~1 1>&2
    goto usage_error
)
set "VERSIONS=!VERSIONS! %~1"
shift
goto parse

:parsed
if not defined VERSIONS (
    rem Built versions that have a Revit to load them.
    for /d %%D in ("%DIST%\*") do (
        echo %%~nxD| findstr /r "^[0-9][0-9][0-9][0-9]$" >nul && (
            if exist "%ProgramFiles%\Autodesk\Revit %%~nxD\Revit.exe" set "VERSIONS=!VERSIONS! %%~nxD"
        )
    )
)
if not defined VERSIONS (
    echo Nothing to install: build first ^(build.cmd^), or name the versions, e.g. install.cmd 2026. 1>&2
    exit /b 1
)

tasklist /fi "IMAGENAME eq Revit.exe" /nh 2>nul | find /i "Revit.exe" >nul && (
    echo WARNING: Revit is running and locks the add-in DLLs. Close it first, then run this again. 1>&2
    exit /b 1
)

for %%V in (%VERSIONS%) do call :one %%V
exit /b 0

:one
set "V=%~1"
set "TARGET=%APPDATA%\Autodesk\Revit\Addins\%V%"
set "MANIFEST=%TARGET%\OpenBimViewer.Revit.addin"
set "FOLDER=%TARGET%\OpenBimViewer"
if defined UNINSTALL (
    if exist "%MANIFEST%" del /f /q "%MANIFEST%"
    if exist "%FOLDER%" rmdir /s /q "%FOLDER%"
    echo Removed from Revit %V%.
    exit /b 0
)
set "SOURCE=%DIST%\%V%"
if not exist "%SOURCE%\OpenBimViewer\OpenBimViewer.Revit.dll" (
    echo WARNING: Revit %V% isn't built; run build.cmd %V% first. 1>&2
    exit /b 0
)
if not exist "%SOURCE%\OpenBimViewer\IfcCollabCore.dll" (
    echo WARNING: Revit %V%: IfcCollabCore.dll is missing from the build, so signing in won't work. Run build.cmd. 1>&2
)
if not exist "%FOLDER%" mkdir "%FOLDER%"
xcopy "%SOURCE%\OpenBimViewer\*" "%FOLDER%\" /e /i /y /q >nul || (
    echo Copying to %FOLDER% failed. 1>&2
    exit /b 1
)
copy /y "%SOURCE%\OpenBimViewer.Revit.addin" "%TARGET%\" >nul || (
    echo Copying the manifest to %TARGET% failed. 1>&2
    exit /b 1
)
echo Installed for Revit %V% in %TARGET%
exit /b 0

:usage
echo Usage: install.cmd [year ...] [/uninstall]
echo   With no years, uses every built version under build\dist whose Revit is installed.
exit /b 0

:usage_error
echo Usage: install.cmd [year ...] [/uninstall] 1>&2
exit /b 2
