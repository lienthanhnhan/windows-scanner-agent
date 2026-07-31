param(
    [string]$AgentDir = "C:\HuyHoangScannerAgent",
    [string]$TaskName = "Huy Hoang Scanner Agent"
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "Please run PowerShell as Administrator, then run this script again."
    }
}

Assert-Administrator

$exePath = Join-Path $AgentDir "HuyHoangScannerAgent.exe"
$scanDir = Join-Path $AgentDir "HuyHoangScans"

if (-not (Test-Path $exePath)) {
    throw "Cannot find $exePath. Please publish the agent and copy it to $AgentDir first."
}

New-Item -ItemType Directory -Force -Path $scanDir | Out-Null

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $AgentDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName

Write-Host "Installed and started '$TaskName' for $currentUser."
Write-Host "Test it in the browser: http://127.0.0.1:9876/health"
