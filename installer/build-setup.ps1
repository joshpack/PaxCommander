<#
.SYNOPSIS
  Builds pxdcomm-setup.exe: pxd Commander as one self-contained file that installs itself.

.DESCRIPTION
  The program runs its installer screen when its file name contains "setup", so the setup file is simply the
  single-file, self-contained build renamed. It runs on any 64-bit Windows 10/11 PC without .NET or admin rights.

  Output: %LOCALAPPDATA%\pxdCommander\installer\pxdcomm-setup.exe (plus a .sha256 beside it), or -OutDir.

.PARAMETER Version
  Optional version stamp (e.g. 1.2.0). Defaults to the <Version> in pxdCommander.csproj.

.PARAMETER OutDir
  Optional output folder. Defaults to %LOCALAPPDATA%\pxdCommander\installer.
#>
param([string]$Version, [string]$OutDir)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\pxdCommander.csproj' | Resolve-Path
$stage = Join-Path $env:TEMP 'pxdcomm-setup-build'
$outDir = if ($OutDir) { $OutDir } else { Join-Path $env:LOCALAPPDATA 'pxdCommander\installer' }
$out = Join-Path $outDir 'pxdcomm-setup.exe'

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

Copy-Item (Join-Path $stage 'pxdCommander.exe') $out -Force
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$out.sha256", "$hash  pxdcomm-setup.exe", [Text.Encoding]::ASCII)  # no newline
Remove-Item $stage -Recurse -Force

$size = '{0:N1} MB' -f ((Get-Item $out).Length / 1MB)
Write-Host "Built $out ($size)"
Write-Host "SHA-256 $hash"
