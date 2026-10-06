<#
.SYNOPSIS
  Produces the self-contained Steam build of Dockable (the part that can be automated), then prints
  the steamcmd command to upload it.

.DESCRIPTION
  Runs `dotnet publish` with the SteamRelease profile into src\Dockable\bin\Publish\Steam\win-x64\.
  Uploading to Steam still requires steamcmd + your Steamworks builder account (Steam Guard is
  interactive), so this script stops at "ready to upload" and tells you the exact command.

.EXAMPLE
  pwsh -File steam\build-steam.ps1
#>
[CmdletBinding()]
param(
    [string]$Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$proj     = Join-Path $repoRoot "src\Dockable\Dockable.csproj"
$outDir   = Join-Path $repoRoot "src\Dockable\bin\Publish\Steam\win-x64"

# Don't let a running instance lock the (separate) publish output.
Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Publishing self-contained Steam build..." -ForegroundColor Cyan
& $Dotnet publish $proj -p:PublishProfile=SteamRelease
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $outDir "Dockable.exe"
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }

$sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "Build ready: $outDir  ($sizeMb MB)" -ForegroundColor Green
Write-Host "Launch exe (Steam launch target): Dockable.exe"
Write-Host ""
Write-Host "Next - upload to Steam (after filling in IDs in steam\app_build.vdf):" -ForegroundColor Cyan
$uploadCmd = 'steamcmd +login BUILDER_ACCOUNT +run_app_build "' + $repoRoot + '\steam\app_build.vdf" +quit'
Write-Host "  $uploadCmd"
