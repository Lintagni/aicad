# Builds both halves of AiCad with the C# compiler that ships with Windows.
# No Visual Studio or .NET SDK required.
#
#   AiCad.dll     - the drawing engine, loaded inside AutoCAD
#   AiCadApp.exe  - the standalone assistant, runs beside AutoCAD
#
#   .\build.ps1
#   .\build.ps1 -AcadDir "C:\Program Files\Autodesk\AutoCAD 2021"

[CmdletBinding()]
param(
    [string]$AcadDir = "",
    [string]$OutDir  = "$PSScriptRoot\build"
)

$ErrorActionPreference = "Stop"

# --- find AutoCAD ------------------------------------------------------------
# Any release works: the build only needs the managed API assemblies, which live
# in the install folder. Newest first, so a machine with several picks the latest.
if (-not $AcadDir) {
    $candidates = @()
    foreach ($root in @("$env:ProgramFiles\Autodesk", "${env:ProgramFiles(x86)}\Autodesk")) {
        if (-not (Test-Path $root)) { continue }
        $candidates += Get-ChildItem $root -Directory -Filter "AutoCAD *" -ErrorAction SilentlyContinue |
                       Where-Object { Test-Path (Join-Path $_.FullName "acmgd.dll") }
    }
    if ($candidates.Count -gt 0) {
        $AcadDir = ($candidates | Sort-Object Name -Descending | Select-Object -First 1).FullName
        Write-Host "AutoCAD: $AcadDir"
    } else {
        throw "No AutoCAD installation found. Pass -AcadDir with the folder containing acmgd.dll."
    }
}

# --- locate the compiler -----------------------------------------------------
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc. Install the .NET Framework 4.x developer tools or use Visual Studio."
}

# --- locate the AutoCAD managed API ------------------------------------------
if (-not (Test-Path $AcadDir)) {
    throw "AutoCAD folder not found: $AcadDir. Pass -AcadDir with the correct path."
}

# AdWindows.dll supplies the ribbon API.
$acadRefs = @()
foreach ($dll in @("acmgd.dll", "acdbmgd.dll", "accoremgd.dll", "AdWindows.dll")) {
    $path = Join-Path $AcadDir $dll
    if (-not (Test-Path $path)) { throw "Missing AutoCAD API assembly: $path" }
    $acadRefs += "/reference:`"$path`""
}

$bclRefs = @()
foreach ($bcl in @("System.dll", "System.Core.dll", "System.Drawing.dll",
                   "System.Windows.Forms.dll", "System.Xaml.dll")) {
    $bclRefs += "/reference:$bcl"
}

# PaletteSet exposes WPF types, so the WPF assemblies must be referenced even
# though the UI itself is WinForms. They live in the framework's WPF subfolder.
$wpfDir = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\WPF"
$wpfRefs = @()
foreach ($wpf in @("WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll")) {
    $path = Join-Path $wpfDir $wpf
    if (Test-Path $path) { $wpfRefs += "/reference:`"$path`"" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Assert-Writable([string]$path) {
    # AutoCAD keeps a NETLOADed assembly locked for the life of the session, so a
    # rebuild while it is running fails with an unhelpful CS0016. Say so plainly.
    if (-not (Test-Path $path)) { return }
    try {
        $handle = [System.IO.File]::Open($path, 'Open', 'Write', 'None')
        $handle.Close()
    } catch {
        throw "$path is locked by a running program. Close AutoCAD, AiCadApp.exe and AiCadServer.exe (the tray icon), then run this again."
    }
}

function Invoke-Csc([string]$target, [string]$outFile, [string[]]$references, [string[]]$sources, [string]$rspName) {
    $args = @("/target:$target", "/platform:x64", "/optimize+", "/warn:3", "/nologo",
              "/out:`"$outFile`"") + $references + $sources
    $rsp = Join-Path $OutDir $rspName
    Set-Content -Path $rsp -Value $args -Encoding utf8
    & $csc "@$rsp"
    if ($LASTEXITCODE -ne 0) { throw "Compilation of $outFile failed with exit code $LASTEXITCODE." }
}

# --- 1. the AutoCAD-side engine ---------------------------------------------
# Only src\AiCad takes part; src\AiCadApp is the standalone app.
$engineSources = Get-ChildItem -Path (Join-Path $PSScriptRoot "src\AiCad") -Filter *.cs -Recurse |
                 ForEach-Object { "`"$($_.FullName)`"" }
if ($engineSources.Count -eq 0) { throw "No engine sources found under src\AiCad." }

$engineDll = Join-Path $OutDir "AiCad.dll"
Assert-Writable $engineDll
Write-Host "Engine : $($engineSources.Count) files -> AiCad.dll"
Invoke-Csc "library" $engineDll ($acadRefs + $bclRefs + $wpfRefs) $engineSources "engine.rsp"

# --- 2. the standalone app ---------------------------------------------------
# Shares the AutoCAD-free sources with the engine; nothing is duplicated.
$sharedFiles = @(
    "src\AiCad\Json\Json.cs",
    "src\AiCad\Config\AiCadConfig.cs",
    "src\AiCad\Model\DrawPlan.cs",
    "src\AiCad\Model\PlanInbox.cs",
    "src\AiCad\Ai\PromptBuilder.cs",
    "src\AiCad\Ai\PlanStream.cs",
    "src\AiCad\Ai\Providers.cs",
    "src\AiCad\UI\SettingsForm.cs"
)
$appSources = @()
foreach ($rel in $sharedFiles) {
    $path = Join-Path $PSScriptRoot $rel
    if (-not (Test-Path $path)) { throw "Shared source missing: $path" }
    $appSources += "`"$path`""
}
$appSources += Get-ChildItem -Path (Join-Path $PSScriptRoot "src\AiCadApp") -Filter *.cs -Recurse |
               ForEach-Object { "`"$($_.FullName)`"" }

$appExe = Join-Path $OutDir "AiCadApp.exe"
Assert-Writable $appExe
Write-Host "App    : $($appSources.Count) files -> AiCadApp.exe"
# winexe, so launching it does not open a console window.
Invoke-Csc "winexe" $appExe $bclRefs $appSources "app.rsp"

# --- 3. the local web server -------------------------------------------------
# Shares the AutoCAD-free sources and the COM bridge with the desktop app; the
# browser provides the UI instead of WinForms.
$serverShared = @(
    "src\AiCad\Json\Json.cs",
    "src\AiCad\Config\AiCadConfig.cs",
    "src\AiCad\Model\DrawPlan.cs",
    "src\AiCad\Model\PlanInbox.cs",
    "src\AiCad\Model\ExampleStore.cs",
    "src\AiCad\Model\ReferenceLibrary.cs",
    "src\AiCad\Model\ImportJob.cs",
    "src\AiCad\Model\ImportProgress.cs",
    "src\AiCad\Model\DrawProgress.cs",
    "src\AiCad\Ai\PromptBuilder.cs",
    "src\AiCad\Ai\PlanStream.cs",
    "src\AiCad\Ai\Providers.cs",
    "src\AiCad\UI\SettingsForm.cs",
    "src\AiCadApp\AcadBridge.cs",
    "src\AiCadApp\ChatModel.cs",
    "src\AiCadApp\ChatStore.cs"
)
$serverSources = @()
foreach ($rel in $serverShared) {
    $path = Join-Path $PSScriptRoot $rel
    if (-not (Test-Path $path)) { throw "Shared source missing: $path" }
    $serverSources += "`"$path`""
}
$serverSources += Get-ChildItem -Path (Join-Path $PSScriptRoot "src\AiCadServer") -Filter *.cs -Recurse |
                  ForEach-Object { "`"$($_.FullName)`"" }

$serverExe = Join-Path $OutDir "AiCadServer.exe"
Assert-Writable $serverExe
Write-Host "Server : $($serverSources.Count) files -> AiCadServer.exe"
Invoke-Csc "winexe" $serverExe $bclRefs $serverSources "server.rsp"

# The UI is plain static files, copied rather than compiled.
$webOut = Join-Path $OutDir "web"
New-Item -ItemType Directory -Force -Path $webOut | Out-Null
Copy-Item (Join-Path $PSScriptRoot "web\*") $webOut -Recurse -Force

Write-Host ""
Write-Host "Build succeeded." -ForegroundColor Green
Write-Host "  $engineDll"
Write-Host "  $appExe"
Write-Host "  $serverExe   (browser UI)"
Write-Host ""
Write-Host "To install both:  .\install.ps1"
Write-Host "To run from here: $appExe   (load the engine once with NETLOAD, or use the app's Load engine button)"
