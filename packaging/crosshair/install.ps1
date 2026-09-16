# Lertaro Crosshair plugin installer (screen crosshair / okiaimx-VALORANT parameter set).
# Usage: right-click -> Run with PowerShell (it self-elevates), or:
#   powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = "Stop"
$pluginName = "Lertaro.Plugins.Crosshair"

# 1. Locate Lertaro install directory.
$candidates = @("$env:ProgramFiles\Lertaro", "${env:ProgramFiles(x86)}\Lertaro")
$lertaro = $null
foreach ($c in $candidates)
{
    if (Test-Path (Join-Path $c "Lertaro.App.exe"))
    {
        $lertaro = $c
        break
    }
}

if (-not $lertaro)
{
    Write-Host "[X] Lertaro not found. Please install Lertaro first." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# 2. Administrator check (Program Files is write-protected).
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    Write-Host "[*] Elevating to administrator..." -ForegroundColor Yellow
    Start-Process -FilePath "powershell.exe" -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit 0
}

# 3. Make sure Lertaro is NOT running: it locks the plugin dll.
#    IMPORTANT: exit Lertaro from the tray icon menu (right click -> Exit).
#    Force-killing Lertaro.App (Stop-Process) can leave the host in a bad state, e.g.
#    the compact search panel may stop opening until the app is cleanly restarted.
while (Get-Process -Name "Lertaro.App" -ErrorAction SilentlyContinue)
{
    Write-Host "[!] Lertaro is running." -ForegroundColor Yellow
    Write-Host "    Please exit it from the tray icon (right click -> Exit), then press Enter here." -ForegroundColor Yellow
    Write-Host "    (type Q and press Enter to abort)" -ForegroundColor Yellow
    $answer = Read-Host
    if ($answer -eq 'q' -or $answer -eq 'Q')
    {
        Write-Host "[X] Aborted, nothing was changed." -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
}

# 4. Copy plugin files.
$dest = Join-Path $lertaro "Plugins\$pluginName"
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$src = Join-Path $PSScriptRoot $pluginName

if (-not (Test-Path (Join-Path $src "$pluginName.dll")))
{
    Write-Host "[X] Plugin files not found next to install.ps1 (expected folder: $pluginName)." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Copy-Item -Path (Join-Path $src "$pluginName.dll") -Destination $dest -Force
Copy-Item -Path (Join-Path $src "$pluginName.pdb") -Destination $dest -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "[OK] Plugin installed to: $dest" -ForegroundColor Green
Write-Host "[*] Start Lertaro, then:"
Write-Host "    Ctrl+Alt+C        toggle the crosshair (global hotkey, configurable)"
Write-Host "    zx k / zx g       open / close the crosshair (prefix follows your trigger keywords)"
Write-Host "    zx                show state + command hints (Tab completes to 'zx k')"
Write-Host "    action menu       'Crosshair editor' - visual okiaimx/VALORANT style editor"
Write-Host "[*] Settings: Lertaro settings -> Plugins -> Screen crosshair."
Read-Host "Press Enter to exit"
