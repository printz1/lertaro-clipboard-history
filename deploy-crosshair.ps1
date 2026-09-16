# Build + deploy the Crosshair plugin into this machine's Lertaro install.
#
# Usage:  powershell -ExecutionPolicy Bypass -File deploy-crosshair.ps1 [-NoBuild]
#
# IMPORTANT: exit Lertaro from the tray icon first (right click -> Exit).
#   Force-killing Lertaro.App (Stop-Process) can leave the host in a bad state
#   (e.g. the compact search panel may stop opening until Lertaro is cleanly restarted).
#   The script waits for you to exit instead of killing the process.

param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$pluginName = 'Lertaro.Plugins.Crosshair'
$repo = $PSScriptRoot
$project = Join-Path $repo "src\$pluginName\$pluginName.csproj"
$log = Join-Path $env:TEMP 'crosshair-deploy-result.txt'

function Report($msg)
{
    Add-Content -Path $log -Value $msg
    Write-Host $msg
}

Set-Content -Path $log -Value ('deploy run at ' + (Get-Date))

try
{
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    # 1. Build first (unless the elevated re-entry tells us to skip it).
    if (-not $NoBuild)
    {
        $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
        if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
        Report "building with $dotnet ..."
        & $dotnet build $project -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }
        Report 'build ok'
    }

    # 2. Locate Lertaro.
    $lertaro = $null
    foreach ($c in @("$env:ProgramFiles\Lertaro", "${env:ProgramFiles(x86)}\Lertaro"))
    {
        if (Test-Path (Join-Path $c 'Lertaro.App.exe')) { $lertaro = $c; break }
    }
    if (-not $lertaro) { throw 'Lertaro install directory not found' }
    $destDir = Join-Path $lertaro "Plugins\$pluginName"

    $src = Join-Path $repo "src\$pluginName\bin\Release\net10.0-windows\$pluginName.dll"
    if (-not (Test-Path $src)) { throw "built dll not found: $src" }

    # 3. Self-elevate for the copy step (Program Files needs admin).
    if (-not $isAdmin)
    {
        Report 'not elevated, relaunching with UAC prompt...'
        Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
            '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-NoBuild')
        return
    }

    Report 'elevated ok'

    # 4. Wait until Lertaro is closed - do NOT force kill it.
    while (Get-Process -Name 'Lertaro.App' -ErrorAction SilentlyContinue)
    {
        Write-Host '[!] Lertaro is running.' -ForegroundColor Yellow
        Write-Host '    Exit it from the tray icon (right click -> Exit), then press Enter here.' -ForegroundColor Yellow
        [void](Read-Host)
    }
    Report 'lertaro closed'

    # 5. Copy.
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Report ('dir ready: ' + $destDir)

    # grant current user modify rights so future deploys need no elevation
    $grant = '{0}:(OI)(CI)M' -f $env:USERNAME
    icacls $destDir /grant $grant /T | Out-Null
    Report ('acl granted: ' + $grant)

    Copy-Item -LiteralPath $src -Destination $destDir -Force
    $deployed = Join-Path $destDir ($pluginName + '.dll')
    $f = Get-Item $deployed
    Report ('deployed: ' + $f.Length + ' bytes, version ' + $f.VersionInfo.FileVersion)
    Report 'OK - now start Lertaro again (tray / shortcut).'
}
catch
{
    Report ('FAILED: ' + $_.Exception.Message)
    Read-Host 'Press Enter to exit'
}
