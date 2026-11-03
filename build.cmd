@echo off
rem Builds Open BIM Viewer for Revit: the native IFC Collab Core DLL, the tests, and the add-in
rem for each Revit version, staged under build\dist\<year>\.
rem Command-console counterpart of build.ps1.
rem
rem   build.cmd                              every supported version (2021-2027)
rem   build.cmd 2026                         just Revit 2026
rem   build.cmd 2024 2027 /skiptests
rem   build.cmd /configuration Debug /skiptests
rem   build.cmd /collabcoredir D:\src\IFCCollabCore
rem   build.cmd /skipnative                  IfcCollabCore.dll is already built
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "VERSIONS="
set "CONFIGURATION=Release"
set "COLLABCOREDIR="
set "SKIPNATIVE="
set "SKIPTESTS="

:parse
if "%~1"=="" goto parsed
set "A=%~1"
rem Accept -name and --name as well as /name.
if "!A:~0,2!"=="--" set "A=/!A:~2!"
if "!A:~0,1!"=="-" set "A=/!A:~1!"
if "!A!"=="/?" goto usage
if /i "!A!"=="/h" goto usage
if /i "!A!"=="/help" goto usage
if /i "!A!"=="/skipnative" (set "SKIPNATIVE=1" & shift & goto parse)
if /i "!A!"=="/skiptests" (set "SKIPTESTS=1" & shift & goto parse)
if /i "!A!"=="/configuration" goto opt_configuration
if /i "!A!"=="/config" goto opt_configuration
if /i "!A!"=="/collabcoredir" goto opt_collabcoredir
echo %~1| findstr /r "^[0-9][0-9][0-9][0-9]$" >nul || (
    echo Unknown argument: %~1 1>&2
    goto usage_error
)
set "VERSIONS=!VERSIONS! %~1"
shift
goto parse

:opt_configuration
if /i "%~2"=="Release" (set "CONFIGURATION=Release") else if /i "%~2"=="Debug" (set "CONFIGURATION=Debug") else (
    echo /configuration takes Release or Debug. 1>&2
    goto usage_error
)
shift & shift & goto parse

:opt_collabcoredir
if "%~2"=="" (
    echo /collabcoredir takes a folder. 1>&2
    goto usage_error
)
set "COLLABCOREDIR=%~2"
shift & shift & goto parse

:parsed
if not defined VERSIONS set "VERSIONS=2021 2022 2023 2024 2025 2026 2027"

if defined SKIPNATIVE goto tests
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" (
    echo Visual Studio ^(with the C++ workload^) is needed to build IfcCollabCore.dll. Install it, or pass /skipnative if the DLL is already built. 1>&2
    exit /b 1
)
set "MSBUILD="
for /f "usebackq delims=" %%M in (`call "!VSWHERE!" -latest -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find MSBuild\**\Bin\MSBuild.exe`) do (
    if not defined MSBUILD set "MSBUILD=%%M"
)
if not defined MSBUILD (
    echo Visual Studio's MSBuild with the C++ tools wasn't found. Add the 'Desktop development with C++' workload. 1>&2
    exit /b 1
)
set "NATIVEARGS=-p:Configuration=%CONFIGURATION% -p:Platform=x64 -nologo -v:minimal"
if defined COLLABCOREDIR set NATIVEARGS=!NATIVEARGS! "-p:CollabCoreDir=!COLLABCOREDIR!"
set "STEP=Native IFC Collab Core (IfcCollabCore.dll)"
echo ==^> !STEP!
"!MSBUILD!" "%ROOT%\native\IfcCollabCoreBridge\IfcCollabCoreBridge.vcxproj" !NATIVEARGS!
if not "!errorlevel!"=="0" goto fail

:tests
if defined SKIPTESTS goto addins
set "STEP=Tests"
echo ==^> !STEP!
dotnet test "%ROOT%\tests\OpenBimViewer.Collab.Tests" -c %CONFIGURATION% -nologo -p:NativeConfiguration=%CONFIGURATION%
if not "!errorlevel!"=="0" goto fail

:addins
for %%V in (%VERSIONS%) do (
    set "STEP=Add-in for Revit %%V"
    echo ==^> !STEP!
    dotnet build "%ROOT%\src\OpenBimViewer.Revit\OpenBimViewer.Revit.csproj" -c %CONFIGURATION% -nologo -v:minimal -p:RevitVersion=%%V -p:NativeConfiguration=%CONFIGURATION%
    if not "!errorlevel!"=="0" goto fail
)

echo.
echo Done. Staged add-ins: %ROOT%\build\dist\^<year^>\. Install with install.cmd
exit /b 0

:fail
set "CODE=!errorlevel!"
echo !STEP! failed (exit code !CODE!). 1>&2
exit /b !CODE!

:usage
echo Usage: build.cmd [year ...] [/configuration Release^|Debug] [/collabcoredir DIR] [/skipnative] [/skiptests]
echo   With no years, builds every supported version (2021-2027).
exit /b 0

:usage_error
echo Usage: build.cmd [year ...] [/configuration Release^|Debug] [/collabcoredir DIR] [/skipnative] [/skiptests] 1>&2
exit /b 2
