<#
.SYNOPSIS
  Builds Open BIM Viewer for Revit: the native IFC Collab Core DLL, the tests, and the add-in
  for each Revit version, staged under build\dist\<year>\.

.EXAMPLE
  .\build.ps1                          # every supported version (2021-2027)
  .\build.ps1 -Versions 2026           # just Revit 2026
  .\build.ps1 -Configuration Debug -SkipTests
  .\build.ps1 -CollabCoreDir D:\src\IFCCollabCore
#>
[CmdletBinding()]
param(
    [int[]] $Versions = @(2021, 2022, 2023, 2024, 2025, 2026, 2027),
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [string] $CollabCoreDir = '',
    [switch] $SkipNative,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Step([string] $title, [scriptblock] $action) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipNative) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Visual Studio (with the C++ workload) is needed to build IfcCollabCore.dll. Install it, or pass -SkipNative if the DLL is already built.' }
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw "Visual Studio's MSBuild with the C++ tools wasn't found. Add the 'Desktop development with C++' workload." }
    $nativeArgs = @("$root\native\IfcCollabCoreBridge\IfcCollabCoreBridge.vcxproj", "-p:Configuration=$Configuration", '-p:Platform=x64', '-nologo', '-v:minimal')
    if ($CollabCoreDir) { $nativeArgs += "-p:CollabCoreDir=$CollabCoreDir" }
    Invoke-Step 'Native IFC Collab Core (IfcCollabCore.dll)' { & $msbuild @nativeArgs }
}

if (-not $SkipTests) {
    Invoke-Step 'Tests' { dotnet test "$root\tests\OpenBimViewer.Collab.Tests" -c $Configuration -nologo -p:NativeConfiguration=$Configuration }
}

foreach ($v in $Versions) {
    Invoke-Step "Add-in for Revit $v" {
        dotnet build "$root\src\OpenBimViewer.Revit\OpenBimViewer.Revit.csproj" -c $Configuration -nologo -v:minimal "-p:RevitVersion=$v" "-p:NativeConfiguration=$Configuration"
    }
}

Write-Host ''
Write-Host "Done. Staged add-ins: $root\build\dist\<year>\. Install with .\install.ps1" -ForegroundColor Green
