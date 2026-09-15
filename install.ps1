# Lertaro Clipboard History plugin installer.
# Usage: right-click -> Run with PowerShell (it self-elevates), or:
#   powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = "Stop"

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
    Write-Host "[X] Lertaro not found. Please install Lertaro 5.6+ first." -ForegroundColor Red
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

# 3. Close Lertaro if running (it locks plugin dll files).
$proc = Get-Process -Name "Lertaro.App" -ErrorAction SilentlyContinue
if ($proc)
{
    Write-Host "[*] Closing Lertaro..." -ForegroundColor Yellow
    $proc | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 4. Copy plugin files.
$dest = Join-Path $lertaro "Plugins\Lertaro.Plugins.ClipboardHistory"
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$src = Join-Path $PSScriptRoot "Lertaro.Plugins.ClipboardHistory"

if (-not (Test-Path (Join-Path $src "Lertaro.Plugins.ClipboardHistory.dll")))
{
    Write-Host "[X] Plugin files not found next to install.ps1 (expected folder: Lertaro.Plugins.ClipboardHistory)." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Copy-Item -Path (Join-Path $src "Lertaro.Plugins.ClipboardHistory.dll") -Destination $dest -Force
Copy-Item -Path (Join-Path $src "Lertaro.Plugins.ClipboardHistory.pdb") -Destination $dest -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "[OK] Plugin installed to: $dest" -ForegroundColor Green
Write-Host "[*] Start Lertaro, then press Ctrl+Shift+V (default hotkey) or type 'cb' in the search box."
Write-Host "[*] Settings: Lertaro settings -> Plugins -> Clipboard History."
Read-Host "Press Enter to exit"
