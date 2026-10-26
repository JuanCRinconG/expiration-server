# Registers ChemClassify ExpirationNotifServer to start at user logon (Windows Task Scheduler).
param(
    [string]$PublishDir = "",
    [string]$TaskName = "ChemClassifyExpirationNotif"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $scriptRoot "publish"
}

$exe = Join-Path $PublishDir "ExpirationNotifServer.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Publishing ExpirationNotifServer to $PublishDir ..."
    dotnet publish (Join-Path $scriptRoot "ExpirationServer.csproj") -c Release -o $PublishDir
}

if (-not (Test-Path $exe)) {
    throw "Could not find $exe after publish."
}

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $PublishDir
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Scheduled task '$TaskName' registered."
Write-Host "  Exe: $exe"
Write-Host "  Trigger: At log on ($env:USERNAME)"
Write-Host "Ensure User env vars are set (SMTP_*, GOOGLE_APPLICATION_CREDENTIALS, EXPIRATION_ALERT_TO, ...)."
Write-Host "Test now: Start-ScheduledTask -TaskName '$TaskName'"
