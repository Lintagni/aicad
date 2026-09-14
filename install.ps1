# Installs AiCad for the current user. No admin rights needed.
#
#   AiCadApp.exe -> %LOCALAPPDATA%\Programs\AiCad   + Start Menu and Desktop shortcuts
#   AiCad.dll    -> %APPDATA%\Autodesk\ApplicationPlugins\AiCad.bundle
#                   so AutoCAD loads the drawing engine on demand, no NETLOAD
#
#   .\install.ps1            install (builds first if needed)
#   .\install.ps1 -Uninstall remove everything this script created

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"

$appDir     = Join-Path $env:LOCALAPPDATA "Programs\AiCad"
$bundleDir  = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AiCad.bundle"
$startMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\AiCad.lnk"
$desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "AiCad.lnk"

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    foreach ($path in @($startMenu, $desktopLnk)) {
        if (Test-Path $path) { Remove-Item $path -Force; Write-Host "Removed $path" }
    }
    foreach ($path in @($appDir, $bundleDir)) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "Removed $path" }
    }
    Write-Host ""
    Write-Host "Uninstalled. Your settings and API key in %APPDATA%\AiCad were left alone." -ForegroundColor Green
    Write-Host "Delete that folder too if you want them gone."
    exit 0
}

# ------------------------------------------------------------------- build
$engineDll = Join-Path $PSScriptRoot "build\AiCad.dll"
$appExe    = Join-Path $PSScriptRoot "build\AiCadApp.exe"
$serverExe = Join-Path $PSScriptRoot "build\AiCadServer.exe"

if (-not (Test-Path $engineDll) -or -not (Test-Path $appExe) -or -not (Test-Path $serverExe)) {
    Write-Host "Build output missing - building first..."
    & (Join-Path $PSScriptRoot "build.ps1")
}

if (Get-Process acad -ErrorAction SilentlyContinue) {
    Write-Warning "AutoCAD is running. The engine will be installed, but AutoCAD must be restarted to pick it up."
}

# --------------------------------------------------------------- app files
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item $appExe $appDir -Force
Copy-Item $serverExe $appDir -Force
# The DLL travels with the app too, so its "Load engine" button has something
# to point AutoCAD at even before a restart.
Copy-Item $engineDll $appDir -Force

# Static UI for the browser version.
$webDir = Join-Path $appDir "web"
New-Item -ItemType Directory -Force -Path $webDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot "build\web\*") $webDir -Recurse -Force
Write-Host "Installed app    -> $appDir"

# ------------------------------------------------------------ autocad bundle
$contents = Join-Path $bundleDir "Contents"
New-Item -ItemType Directory -Force -Path $contents | Out-Null
Copy-Item (Join-Path $PSScriptRoot "bundle\AiCad.bundle\PackageContents.xml") $bundleDir -Force

# AutoCAD locks a loaded engine for the life of its session. The app half is
# already installed by this point, so report clearly and carry on rather than
# failing the whole install.
$engineInstalled = $false
try {
    Copy-Item $engineDll $contents -Force -ErrorAction Stop
    $engineInstalled = $true
    Write-Host "Installed engine -> $bundleDir"
} catch {
    Write-Warning "The engine could not be replaced because AutoCAD has it loaded."
    Write-Warning "The app was updated. Close AutoCAD and run install.ps1 again to update the engine."
}

# ------------------------------------------------------------------ shortcuts
function New-Shortcut([string]$linkPath, [string]$target, [string]$workingDir, [string]$description) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $target
    $link.WorkingDirectory = $workingDir
    $link.Description = $description
    $link.Save()
    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

# The browser version is the one the shortcuts point at; the WinForms app
# stays installed as a fallback.
$installedExe = Join-Path $appDir "AiCadServer.exe"
New-Shortcut $startMenu $installedExe $appDir "AiCad drawing assistant for AutoCAD"
Write-Host "Start Menu       -> $startMenu"

$desktopApp = Join-Path ([Environment]::GetFolderPath("Desktop")) "AiCad (window).lnk"
New-Shortcut $desktopApp (Join-Path $appDir "AiCadApp.exe") $appDir "AiCad desktop window version"
Write-Host "Desktop (window) -> $desktopApp"

if (-not $NoDesktopShortcut) {
    New-Shortcut $desktopLnk $installedExe $appDir "AiCad drawing assistant for AutoCAD"
    Write-Host "Desktop          -> $desktopLnk"
}

Write-Host ""
if ($engineInstalled) {
    Write-Host "Installed." -ForegroundColor Green
} else {
    Write-Host "App installed; engine skipped (AutoCAD had it open)." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Next:"
Write-Host "  1. Restart AutoCAD so it picks up the engine bundle."
Write-Host "  2. Launch AiCad from the Start Menu (or the desktop shortcut)."
Write-Host "  3. The banner turns green once it finds AutoCAD. Type a request and press Ctrl+Enter."
