# Lertaro Clipboard History plugin installer.
# Usage: right-click -> Run with PowerShell (it self-elevates), or:
#   powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = "Stop"
$pluginName = "Lertaro.Plugins.ClipboardHistory"

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
#    Do NOT force-kill Lertaro.App (Stop-Process): that can leave the host in a bad
#    state where the compact search panel stops opening until the app is cleanly restarted.
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
Write-Host "[*] Start Lertaro, then press Ctrl+Shift+V (default hotkey) or type 'cb' in the search box."
Write-Host "[*] Settings: Lertaro settings -> Plugins -> Clipboard History."
Read-Host "Press Enter to exit"
