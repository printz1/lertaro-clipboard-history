[CmdletBinding()]
param(
    [int]$MaxAttempts = 5,
    [int]$StartupWaitSec = 12
)

# Fix for Lertaro hotkey (Win+V) dead-on-startup race.
#
# Symptom: after starting Lertaro, Win+V does nothing. Evidence from logs:
# a healthy hook process writes hook.log within ~1 second of launch
# (ServicePluginLoader lines); a stuck hook process stays alive with an
# EMPTY hook.log forever - the hotkey never gets registered.
#
# This script detects the stuck state (hook alive + empty hook.log), does a
# clean restart of Lertaro, and retries until the hook starts healthy.
#
# Usage (run from an ELEVATED PowerShell - killing the elevated hook needs it):
#   powershell -ExecutionPolicy Bypass -File fix-hook.ps1
#   powershell -ExecutionPolicy Bypass -File fix-hook.ps1 -MaxAttempts 8

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:LOCALAPPDATA 'Lertaro\logs'
$hookLog = Join-Path $logDir 'hook.log'
$appExe = 'C:\Program Files\Lertaro\Lertaro.App.exe'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: please run this script from an ELEVATED PowerShell (the hook process runs elevated).'
    exit 1
}

function Get-HookProcess {
    Get-CimInstance Win32_Process -Filter "Name = 'Lertaro.Service.exe'" |
        Where-Object { $_.CommandLine -match '--hook' } |
        Select-Object -First 1
}

function Test-HookHealthy {
    $hook = Get-HookProcess
    if (-not $hook) { return $false }
    if (-not (Test-Path $hookLog)) { return $false }
    return (Get-Item $hookLog).Length -gt 0
}

function Stop-Lertaro {
    foreach ($name in @('Lertaro.App', 'Lertaro.Service')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

Write-Host 'Lertaro hotkey (Win+V) startup fix'
Write-Host "hook.log: $hookLog"
Write-Host ''

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Host "=== Attempt $attempt of $MaxAttempts ==="

    $hook = Get-HookProcess
    if ($hook) {
        Write-Host ("hook process alive: PID " + $hook.ProcessId)
        if (Test-HookHealthy) {
            Write-Host 'hook.log has content -> hotkey is working. DONE.'
            exit 0
        }
        Write-Host 'hook.log is EMPTY -> hook is stuck in startup race. Killing Lertaro for a clean restart...'
        Stop-Lertaro
    }
    else {
        $app = Get-Process -Name 'Lertaro.App' -ErrorAction SilentlyContinue
        if (-not $app) {
            Write-Host 'Lertaro is not running. Starting it...'
            Start-Process -FilePath $appExe
            Start-Sleep -Seconds 5
        }
        else {
            Write-Host 'App is running but no hook process yet; waiting for the service to launch it...'
        }
    }

    Write-Host ("Waiting up to $StartupWaitSec s for a healthy hook...")
    $deadline = (Get-Date).AddSeconds($StartupWaitSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Test-HookHealthy) {
            Write-Host 'hook started healthy -> hotkey (Win+V) is working. DONE.'
            exit 0
        }
    }

    Write-Host 'hook did not become healthy in time, retrying...'
    Write-Host ''
}

Write-Host "FAILED after $MaxAttempts attempts. Check C:\ProgramData\Lertaro\logs\service.log and try again later."
exit 1
