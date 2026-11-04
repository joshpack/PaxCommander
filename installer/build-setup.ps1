<#
.SYNOPSIS
  Builds paxcomm-setup.exe: PAX Commander as one self-contained file that installs itself.

.DESCRIPTION
  The program runs its installer screen when its file name contains "setup", so the setup file is simply the
  single-file, self-contained build renamed. It runs on any 64-bit Windows 10/11 PC without .NET or admin rights.

  Output: %LOCALAPPDATA%\PaxCommander\installer\paxcomm-setup.exe (plus a .sha256 beside it).

.PARAMETER Version
  Optional version stamp (e.g. 1.2.0). Defaults to the <Version> in PaxCommander.csproj.
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\PaxCommander.csproj' | Resolve-Path
$stage = Join-Path $env:TEMP 'paxcomm-setup-build'
$outDir = Join-Path $env:LOCALAPPDATA 'PaxCommander\installer'
$out = Join-Path $outDir 'paxcomm-setup.exe'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $outDir | Out-Null

$props = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-o', $stage, '-nologo', '-v', 'q'
)
if ($Version) { $props += "-p:Version=$Version" }

Write-Host "Building (full rebuild: the project lives on a NAS share whose clock drifts, so never trust stale outputs)…"
dotnet build $project @($props | Where-Object { $_ -notin @('-o', $stage) }) --no-incremental
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
Write-Host "Publishing self-contained single file…"
dotnet publish $project @props --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $stage 'PaxCommander.exe') $out -Force
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  paxcomm-setup.exe" | Set-Content -Encoding ascii "$out.sha256"
Remove-Item $stage -Recurse -Force

$size = '{0:N1} MB' -f ((Get-Item $out).Length / 1MB)
Write-Host "Built $out ($size)"
Write-Host "SHA-256 $hash"
