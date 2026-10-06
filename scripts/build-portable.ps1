<#
.SYNOPSIS
  Publishes Dockable as a single portable executable.

.DESCRIPTION
  Runs `dotnet publish` with the Portable profile, producing one self-contained Dockable.exe under
  src\Dockable\bin\Publish\Portable\win-x64\. It runs on any Windows 11 x64 machine with nothing
  installed — copy the single .exe anywhere and run it.

.EXAMPLE
  pwsh -File scripts\build-portable.ps1
#>
[CmdletBinding()]
param(
    [string]$Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$proj     = Join-Path $repoRoot "src\Dockable\Dockable.csproj"
$outDir   = Join-Path $repoRoot "src\Dockable\bin\Publish\Portable\win-x64"

# Don't let a running instance lock the (separate) publish output.
Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Publishing single-file portable build..." -ForegroundColor Cyan
& $Dotnet publish $proj -p:PublishProfile=Portable
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $outDir "Dockable.exe"
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$loose  = Get-ChildItem $outDir -File | Where-Object { $_.Name -ne "Dockable.exe" }
Write-Host ""
Write-Host "Portable build ready: $exe  ($sizeMb MB)" -ForegroundColor Green
if ($loose) {
    Write-Host "Note: extra files were produced alongside the exe:" -ForegroundColor Yellow
    $loose | ForEach-Object { Write-Host "  $($_.Name)" }
} else {
    Write-Host "It's a single file — copy Dockable.exe anywhere and run it."
}
