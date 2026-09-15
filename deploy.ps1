[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$LertaroRoot = 'C:\Program Files\Lertaro',
    [switch]$NoWait
)

$ErrorActionPreference = 'Stop'

$pluginName = 'Lertaro.Plugins.ClipboardHistory'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildOut = Join-Path $repoRoot ("src\{0}\bin\{1}\net10.0-windows" -f $pluginName, $Configuration)
$destDir = Join-Path $LertaroRoot ("Plugins\{0}" -f $pluginName)
$sourceDll = Join-Path $buildOut ($pluginName + '.dll')

if (-not (Test-Path $sourceDll)) {
    throw "Build output not found: $sourceDll`nRun the build first (see below)."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not (Test-Path $destDir)) {
    if (-not $isAdmin) {
        Write-Host ''
        Write-Host 'The plugin folder does not exist yet, and creating it needs administrator rights:'
        Write-Host ('  ' + $destDir)
        Write-Host ''
        Write-Host 'Run the following once in an ELEVATED PowerShell. It creates the folder and grants'
        Write-Host 'your account write access, so every later deploy works without elevation:'
        Write-Host ''
        Write-Host ('  New-Item -ItemType Directory -Force "{0}" | Out-Null' -f $destDir)
        Write-Host ('  icacls "{0}" /grant "{1}:(OI)(CI)M" /T' -f $destDir, $env:USERNAME)
        Write-Host ''
        Write-Host 'Then re-run this script.'
        exit 1
    }

    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Write-Host ('Created plugin folder: ' + $destDir)
}

$targetDll = Join-Path $destDir ($pluginName + '.dll')

if (Test-Path $targetDll) {
    # Native plugins are loaded in-process, so a running host blocks writing to the dll.
    # Try a write-open first; if that fails, fall back to renaming the old file aside
    # (rename still works when the file is merely held open for reading).
    $locked = $false
    try {
        $probe = [System.IO.File]::Open($targetDll, 'Open', 'ReadWrite', 'None')
        $probe.Close()
    }
    catch {
        $locked = $true
    }

    if ($locked) {
        $staleDir = Join-Path $env:TEMP 'lertaro-plugin-stale'
        if (-not (Test-Path $staleDir)) {
            New-Item -ItemType Directory -Path $staleDir -Force | Out-Null
        }

        $stale = Join-Path $staleDir ($pluginName + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dll')

        try {
            Move-Item -LiteralPath $targetDll -Destination $stale -Force
            Write-Host ('DLL was held open; moved old copy aside to:')
            Write-Host ('  ' + $stale)
            Write-Host 'Start Lertaro only after this deploy finishes.'
        }
        catch {
            Write-Host ''
            Write-Host 'Target DLL is locked and cannot be replaced or renamed:'
            Write-Host ('  ' + $targetDll)
            Write-Host ''
            Write-Host 'Exit Lertaro completely (tray icon -> Exit), then run this script again.'
            exit 2
        }
    }
}

try {
    Copy-Item -LiteralPath $sourceDll -Destination $destDir -Force
    Write-Host ('Deployed: ' + $targetDll)
}
catch [System.UnauthorizedAccessException] {
    Write-Host ''
    Write-Host 'Access denied while copying. Grant write access once from an elevated PowerShell:'
    Write-Host ('  icacls "{0}" /grant "{1}:(OI)(CI)M" /T' -f $destDir, $env:USERNAME)
    exit 1
}

$pdb = Join-Path $buildOut ($pluginName + '.pdb')
if (Test-Path $pdb) {
    Copy-Item -LiteralPath $pdb -Destination $destDir -Force
}

$deployed = Join-Path $destDir ($pluginName + '.dll')
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($deployed)
Write-Host ('Version : ' + $info.FileVersion)
Write-Host ('Size    : ' + (Get-Item $deployed).Length + ' bytes')
Write-Host ('SHA256  : ' + (Get-FileHash $deployed -Algorithm SHA256).Hash)

if (-not $NoWait) {
    Write-Host ''
    Write-Host 'Start Lertaro.App, then type "cb" in the search box.'
}
