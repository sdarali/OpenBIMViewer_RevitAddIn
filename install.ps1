<#
.SYNOPSIS
  Installs (or removes) the staged add-in for the current Windows user:
  build\dist\<year>\ → %APPDATA%\Autodesk\Revit\Addins\<year>\.

.EXAMPLE
  .\install.ps1                    # every built version whose Revit is installed
  .\install.ps1 -Versions 2026     # just Revit 2026 (installed or not)
  .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [int[]] $Versions,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'build\dist'

if (-not $Versions) {
    # Built versions that have a Revit to load them.
    $Versions = @(Get-ChildItem $dist -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}$' -and (Test-Path "$env:ProgramFiles\Autodesk\Revit $($_.Name)\Revit.exe") } |
        ForEach-Object { [int] $_.Name })
    if (-not $Versions) { throw "Nothing to install: build first (.\build.ps1), or name the versions with -Versions." }
}

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit is running and locks the add-in DLLs. Close it first, then run this again.'
    exit 1
}

foreach ($v in $Versions) {
    $target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $manifest = Join-Path $target 'OpenBimViewer.Revit.addin'
    $folder = Join-Path $target 'OpenBimViewer'
    if ($Uninstall) {
        Remove-Item $manifest -ErrorAction SilentlyContinue -Confirm:$false
        Remove-Item $folder -Recurse -ErrorAction SilentlyContinue -Confirm:$false
        Write-Host "Removed from Revit $v."
        continue
    }
    $source = Join-Path $dist $v
    if (-not (Test-Path (Join-Path $source 'OpenBimViewer\OpenBimViewer.Revit.dll'))) {
        Write-Warning "Revit $v isn't built; run .\build.ps1 -Versions $v first."
        continue
    }
    if (-not (Test-Path (Join-Path $source 'OpenBimViewer\IfcCollabCore.dll'))) {
        Write-Warning "Revit ${v}: IfcCollabCore.dll is missing from the build, so signing in won't work. Run .\build.ps1."
    }
    New-Item -ItemType Directory -Force $folder | Out-Null
    Copy-Item (Join-Path $source 'OpenBimViewer\*') $folder -Force
    Copy-Item (Join-Path $source 'OpenBimViewer.Revit.addin') $target -Force
    Write-Host "Installed for Revit $v in $target"
}
