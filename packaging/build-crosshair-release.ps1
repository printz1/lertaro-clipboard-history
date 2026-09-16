# Build a release zip for the Crosshair plugin: release\Crosshair-<version>.zip
# Layout mirrors the clipboard plugin zips:
#   Lertaro.Plugins.Crosshair\Lertaro.Plugins.Crosshair.dll (+ .pdb)
#   install.ps1
#   README.md
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging\build-crosshair-release.ps1
#         (add -SkipBuild to package the already built dll)

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Lertaro.Plugins.Crosshair\Lertaro.Plugins.Crosshair.csproj'
$pluginName = 'Lertaro.Plugins.Crosshair'

# 1. Build (prefer a .NET 10 SDK: the user-local one first, then PATH).
if (-not $SkipBuild)
{
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        'dotnet'
    )

    $dotnet = $null
    foreach ($c in $candidates)
    {
        if ($c -eq 'dotnet' -or (Test-Path $c))
        {
            $dotnet = $c
            break
        }
    }

    Write-Host "[*] Building with $dotnet ..."
    & $dotnet build $project -c Release | Out-Host
    if ($LASTEXITCODE -ne 0)
    {
        throw "build failed with exit code $LASTEXITCODE"
    }
}

$binDir = Join-Path $repo "src\$pluginName\bin\Release\net10.0-windows"
$dll = Join-Path $binDir "$pluginName.dll"
if (-not (Test-Path $dll))
{
    throw "built dll not found: $dll"
}

# Version comes from the csproj (keep <Version> in sync).
# FileVersion is 4-part (0.3.0.0); release zips use the 3-part form (0.3.0) like the clipboard ones.
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

# 2. Stage the package in a temp dir.
$stage = Join-Path $env:TEMP "crosshair-pkg-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$pluginDir = Join-Path $stage $pluginName
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

Copy-Item $dll $pluginDir -Force
$pdb = Join-Path $binDir "$pluginName.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $pluginDir -Force }

Copy-Item (Join-Path $PSScriptRoot 'crosshair\install.ps1') $stage -Force
Copy-Item (Join-Path $PSScriptRoot 'crosshair\README.md') $stage -Force

# 3. Zip it into release\.
$releaseDir = Join-Path $repo 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$zip = Join-Path $releaseDir "Crosshair-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

Write-Host ""
Write-Host "[OK] $zip" -ForegroundColor Green
Write-Host ("     {0:N0} bytes" -f (Get-Item $zip).Length)
Remove-Item $stage -Recurse -Force
