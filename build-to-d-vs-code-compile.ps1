<#
.SYNOPSIS
    Builds the EFT DMA Radar (read-only version) and outputs to D:\VScodeCompile
.DESCRIPTION
    Run this script from the repository root after ensuring you have:
    - Visual Studio 2022 17.12+ with .NET desktop development workload, OR
    - .NET 10 SDK installed
    - The project targets net10.0-windows (x64)
#>

[CmdletBinding()]
param(
    [string]$OutputPath = "D:\VScodeCompile",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "=== EFT DMA Radar - Build to $OutputPath ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Gray

# Ensure we are in the repo root (where the .sln lives)
$repoRoot = $PSScriptRoot
if (-not (Test-Path "$repoRoot\eft-dma-radar-silk.sln")) {
    Write-Error "This script must be run from the repository root (the folder containing eft-dma-radar-silk.sln)."
    exit 1
}

Set-Location $repoRoot

# Clean previous output
if (Test-Path $OutputPath) {
    Write-Host "Cleaning previous output at $OutputPath..." -ForegroundColor Yellow
    Remove-Item -Path $OutputPath -Recurse -Force -ErrorAction SilentlyContinue
}

# Restore + Build + Publish the main project
Write-Host "`nRestoring packages..." -ForegroundColor Green
dotnet restore "eft-dma-radar-silk.sln" --nologo

Write-Host "`nPublishing eft-dma-radar (Release, win-x64) to $OutputPath ..." -ForegroundColor Green

$publishArgs = @(
    "publish",
    "src-silk\eft-dma-radar.csproj",
    "-c", $Configuration,
    "-r", "win-x64",
    "--no-restore",
    "-o", $OutputPath,
    "/p:PublishSingleFile=false",
    "/p:SelfContained=false",
    "/p:DebugType=portable",
    "--nologo"
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "`n=== Build completed successfully! ===" -ForegroundColor Green
Write-Host "Output location: $OutputPath" -ForegroundColor Cyan

# Show what was produced
Write-Host "`nContents of output folder:" -ForegroundColor Gray
Get-ChildItem $OutputPath -File | Select-Object Name, Length | Format-Table -AutoSize

Write-Host @"

IMPORTANT NOTES:
- This is the read-only version (memory write features removed).
- You still need .NET 10 Desktop Runtime (x64) on the target machine.
- The native DMA files (vmm.dll, leechcore.dll, etc.) are included from lib/VmmSharpEx/native.
- Run with: dotnet run --project src-silk\eft-dma-radar.csproj -c Release   (for development)
  Or just launch eft-dma-radar.exe from the output folder.

"@ -ForegroundColor Yellow
