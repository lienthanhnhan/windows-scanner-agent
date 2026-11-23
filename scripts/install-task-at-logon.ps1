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
$trigger.Delay = "PT1M"
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
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

$protocolKey = "HKCU:\Software\Classes\huyhoang-scanner"
$commandKey = Join-Path $protocolKey "shell\open\command"
$AgentDir = (Get-Item -LiteralPath $AgentDir).FullName.TrimEnd('\')
$exePath = Join-Path $AgentDir "HuyHoangScannerAgent.exe"
New-Item -Path $commandKey -Force | Out-Null
Set-Item -LiteralPath $protocolKey -Value "URL:Huy Hoang Scanner Agent"
New-ItemProperty -LiteralPath $protocolKey -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null
# Register a fixed command without passing browser-supplied URL arguments.
$launchScript = "Start-Process -FilePath '$($exePath.Replace("'", "''"))' -WorkingDirectory '$($AgentDir.Replace("'", "''"))' -WindowStyle Hidden"
$encodedLaunch = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launchScript))
$powerShellExe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
Set-Item -LiteralPath $commandKey -Value ('"{0}" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {1}' -f $powerShellExe, $encodedLaunch)

Write-Host "Installed and started '$TaskName' for $currentUser."
Write-Host "Test it in the browser: http://127.0.0.1:9876/health"
