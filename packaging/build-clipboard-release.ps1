# Build a release zip for the Clipboard History plugin: release\ClipboardHistory-<version>.zip
# Layout mirrors the existing zips:
#   Lertaro.Plugins.ClipboardHistory\Lertaro.Plugins.ClipboardHistory.dll (+ .pdb)
#   install.ps1
#   README.md
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging\build-clipboard-release.ps1 [-SkipBuild]

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Lertaro.Plugins.ClipboardHistory\Lertaro.Plugins.ClipboardHistory.csproj'
$pluginName = 'Lertaro.Plugins.ClipboardHistory'

if (-not $SkipBuild)
{
    $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
    Write-Host "[*] Building with $dotnet ..."
    & $dotnet build $project -c Release -p:LertaroSkipDeploy=true | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }
}

$binDir = Join-Path $repo "src\$pluginName\bin\Release\net10.0-windows"
$dll = Join-Path $binDir "$pluginName.dll"
if (-not (Test-Path $dll)) { throw "built dll not found: $dll" }

$version = (Get-Item $dll).VersionInfo.FileVersion
if ($version)
{
    $parts = $version.Trim().Split('.')
    if ($parts.Count -eq 4 -and $parts[3] -eq '0') { $version = ($parts[0..2] -join '.') }
    else { $version = $version.Trim() }
}
else
{
    $version = '0.0.0'
}

$stage = Join-Path $env:TEMP "clipboard-pkg-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$pluginDir = Join-Path $stage $pluginName
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

Copy-Item $dll $pluginDir -Force
$pdb = Join-Path $binDir "$pluginName.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $pluginDir -Force }

Copy-Item (Join-Path $PSScriptRoot 'clipboard\install.ps1') $stage -Force
Copy-Item (Join-Path $PSScriptRoot 'clipboard\README.md') $stage -Force

$releaseDir = Join-Path $repo 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$zip = Join-Path $releaseDir "ClipboardHistory-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

Write-Host ""
Write-Host "[OK] $zip" -ForegroundColor Green
Write-Host ("     {0:N0} bytes" -f (Get-Item $zip).Length)
Remove-Item $stage -Recurse -Force
