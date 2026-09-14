# Compiles and runs the headless checks (JSON layer + sample plan).
# These need no AutoCAD, so they can run on any machine.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc." }

$outDir = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir "AiCad.Tests.exe"

# Only the AutoCAD-free sources take part.
$sources = @(
    (Join-Path $root "src\AiCad\Json\Json.cs"),
    (Join-Path $root "src\AiCad\Model\SamplePlan.cs"),
    (Join-Path $root "src\AiCad\Model\PlanInbox.cs"),
    (Join-Path $root "src\AiCad\Config\AiCadConfig.cs"),
    (Join-Path $root "src\AiCad\Model\DrawPlan.cs"),
    (Join-Path $root "src\AiCad\Model\ExampleStore.cs"),
    (Join-Path $root "src\AiCad\Ai\PromptBuilder.cs"),
    (Join-Path $root "src\AiCad\Generators\TextFit.cs"),
    (Join-Path $root "src\AiCad\Ai\PlanStream.cs"),
    (Join-Path $PSScriptRoot "HeadlessTests.cs")
) | ForEach-Object { "`"$_`"" }

& $csc /nologo /target:exe /platform:x64 /out:"$exe" /reference:System.dll /reference:System.Core.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Test build failed." }

& $exe
exit $LASTEXITCODE
