# Starts AiCad, building it first if needed.
#
# Called by AiCad.cmd. Everything a first-time user needs happens here:
#   1. build, if the binaries are missing or older than the source
#   2. install the drawing engine where AutoCAD looks for plug-ins
#   3. start the server, which opens the browser
#
# Nothing is installed system-wide and no administrator rights are needed.

[CmdletBinding()]
param(
    [string]$AcadDir = "",
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$build = Join-Path $root "build"
$serverExe = Join-Path $build "AiCadServer.exe"

function Test-NeedsBuild {
    if ($Rebuild) { return $true }
    if (-not (Test-Path $serverExe)) { return $true }

    # Rebuild when any source is newer than the binary we would run.
    $built = (Get-Item $serverExe).LastWriteTimeUtc
    $newest = Get-ChildItem (Join-Path $root "src") -Filter *.cs -Recurse -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTimeUtc -gt $built) { return $true }
    return $false
}

if (Test-NeedsBuild) {
    Write-Host "First run: building AiCad for your AutoCAD..." -ForegroundColor Cyan
    Write-Host "(no Visual Studio or .NET SDK needed - this uses the compiler already on Windows)"
    Write-Host ""

    $buildArgs = @{}
    if ($AcadDir) { $buildArgs["AcadDir"] = $AcadDir }
    & (Join-Path $root "build.ps1") @buildArgs
    Write-Host ""
}

if (-not (Test-Path $serverExe)) {
    throw "The build did not produce $serverExe."
}

# The web UI is served from disk beside the exe.
$webSource = Join-Path $root "web"
$webTarget = Join-Path $build "web"
if (Test-Path $webSource) {
    New-Item -ItemType Directory -Force -Path $webTarget | Out-Null
    Copy-Item (Join-Path $webSource "*") $webTarget -Recurse -Force
}

# Put the engine where AutoCAD looks. Skipped silently when AutoCAD has it open;
# the running copy is then already the right one, or the user restarts AutoCAD.
$bundle = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AiCad.bundle"
$contents = Join-Path $bundle "Contents"
try {
    New-Item -ItemType Directory -Force -Path $contents | Out-Null
    Copy-Item (Join-Path $root "bundle\AiCad.bundle\PackageContents.xml") $bundle -Force
    Copy-Item (Join-Path $build "AiCad.dll") $contents -Force -ErrorAction Stop
    Write-Host "Drawing engine installed for AutoCAD." -ForegroundColor Green
} catch {
    Write-Host "AutoCAD currently has the engine loaded; keeping the installed copy." -ForegroundColor Yellow
}

# The engine DLL travels beside the server too, for the "Load engine" button.
Copy-Item (Join-Path $build "AiCad.dll") $build -Force -ErrorAction SilentlyContinue

if (Get-Process AiCadServer -ErrorAction SilentlyContinue) {
    Write-Host "AiCad is already running - look for the icon in the system tray." -ForegroundColor Yellow
    exit 0
}

Write-Host "Starting AiCad. Your browser will open." -ForegroundColor Green
Start-Process $serverExe -WorkingDirectory $build
