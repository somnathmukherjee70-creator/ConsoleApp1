# Installs (or with -Uninstall, removes) the Revit Geo Export add-in for every installed
# Revit version from 2024 on. Run install.bat, or:
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Uninstall]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

# Revit 2024 runs .NET Framework 4.8; 2025 and later run .NET 8.
$builds = @{ '2024' = 'Revit2024'; '2025' = 'Revit2025'; '2026' = 'Revit2025'; '2027' = 'Revit2025' }

$found = $false
foreach ($version in ($builds.Keys | Sort-Object)) {
    $installed = (Test-Path "C:\Program Files\Autodesk\Revit $version") -or
                 (Test-Path (Join-Path $addinsRoot $version)) -or
                 (Test-Path "$env:ProgramData\Autodesk\Revit\Addins\$version")
    if (-not $installed) { continue }
    $found = $true

    $target = Join-Path $addinsRoot $version
    $manifest = Join-Path $target 'RevitGeoExport.addin'
    $folder = Join-Path $target 'RevitGeoExport'

    if ($Uninstall) {
        Remove-Item $manifest -Force -ErrorAction SilentlyContinue
        Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed from Revit $version"
        continue
    }

    $source = Join-Path $here $builds[$version]
    if (-not (Test-Path (Join-Path $source 'RevitGeoExport.dll'))) {
        Write-Warning "Revit ${version}: build files not found in $source, skipped."
        continue
    }

    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item (Join-Path $source '*') $folder -Recurse -Force
    Copy-Item (Join-Path $here 'RevitGeoExport.addin') $manifest -Force

    # Files from a downloaded ZIP are marked as coming from the internet; Revit won't load them.
    Get-ChildItem $folder -Recurse | Unblock-File
    Unblock-File $manifest

    Write-Host "Installed for Revit $version -> $target"
}

if (-not $found) {
    Write-Warning 'No Revit 2024 or later was found on this computer.'
} elseif (-not $Uninstall) {
    Write-Host ''
    Write-Host 'Done. Close Revit if it is open, start it again, and choose "Always Load" when asked about the add-in.'
    Write-Host 'The button is on the Add-Ins tab: Geo Export > Place on Map.'
}
