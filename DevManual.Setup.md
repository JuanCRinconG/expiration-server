# Expiration notification server — setup

Standalone **.NET 8** console repo (not part of ChemClassify / Godot). Once per day at a configurable local hour (default **12:00**), it queries Firestore for active SDS with `expiresAt` within the notify window (and already expired), then emails a digest via the local `Mail` / `MailData` copies under namespace `ExpirationServer`.

## Layout

| File | Role |
| --- | --- |
| `ExpirationNotifServer.cs` | Entry point / daily daemon |
| `Mail.cs` | SMTP send + `MailSendResult` |
| `MailData.cs` | Message payload |
| `LoginValidation.cs` | Email domain check used by `MailData.Verify` |
| `Register-Autostart.ps1` | Windows Task Scheduler at logon |
| `ExpirationServer.csproj` | Console project |

All C# types live in namespace **`ExpirationServer`**.

## Prerequisites

| Variable | Required | Purpose |
| --- | --- | --- |
| `GOOGLE_APPLICATION_CREDENTIALS` | yes | Service account JSON path |
| `FIREBASE_PROJECT_ID` | yes | Firestore project id (`GOOGLE_CLOUD_PROJECT` / `GCLOUD_PROJECT` also work) |
| `EXPIRATION_ALERT_TO` | yes | One or more recipient emails, comma-separated |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASS` / `SMTP_FROM` | yes | SMTP |
| `EXPIRATION_TIMEZONE` | no | Time zone id (default `UTC`) |
| `EXPIRATION_RUN_HOUR` | no | Local hour `0`–`23` (default `12`) |
| `EXPIRATION_NOTIFY_WINDOW_DAYS` | no | Days ahead (default `30`) |
| `ALLOWED_DOMAIN` | no | Only if you call `MailData.Verify` |

Firestore index: collection `sds`, fields `active` Asc + `expiresAt` Asc (see ChemClassify `FirebaseSetupPlan.md`).

### Time zone examples

| OS | Example id |
| --- | --- |
| Windows (Colombia) | `SA Pacific Standard Time` |
| Linux / macOS | `America/Bogota` |
| UTC | `UTC` |

### Set alert recipient (PowerShell)

```powershell
[System.Environment]::SetEnvironmentVariable("EXPIRATION_ALERT_TO", "lab-manager@universidad.edu", "User")
[System.Environment]::SetEnvironmentVariable("EXPIRATION_TIMEZONE", "SA Pacific Standard Time", "User")
[System.Environment]::SetEnvironmentVariable("EXPIRATION_RUN_HOUR", "12", "User")
[System.Environment]::SetEnvironmentVariable("EXPIRATION_NOTIFY_WINDOW_DAYS", "30", "User")

$env:EXPIRATION_ALERT_TO = [System.Environment]::GetEnvironmentVariable("EXPIRATION_ALERT_TO", "User")
$env:EXPIRATION_TIMEZONE = [System.Environment]::GetEnvironmentVariable("EXPIRATION_TIMEZONE", "User")
$env:EXPIRATION_RUN_HOUR = [System.Environment]::GetEnvironmentVariable("EXPIRATION_RUN_HOUR", "User")
$env:EXPIRATION_NOTIFY_WINDOW_DAYS = [System.Environment]::GetEnvironmentVariable("EXPIRATION_NOTIFY_WINDOW_DAYS", "User")
```

## Build / run

```powershell
cd <this-repo>
dotnet build .\ExpirationServer.csproj -c Release

# One-shot test
dotnet run --project .\ExpirationServer.csproj -c Release -- --once

# Daemon
dotnet run --project .\ExpirationServer.csproj -c Release
```

Publish:

```powershell
dotnet publish .\ExpirationServer.csproj -c Release -o .\publish
.\publish\ExpirationNotifServer.exe --once
```

## Autostart (Windows VM)

Prefer **At log on** so User environment variables are available.

```powershell
.\Register-Autostart.ps1
```

Or Task Scheduler manually: trigger At log on → start `ExpirationNotifServer.exe` (no `--once`).

Remove:

```powershell
Unregister-ScheduledTask -TaskName "ChemClassifyExpirationNotif" -Confirm:$false
```

## Notes

- Query: `active == true` AND `expiresAt <= (UTC today + window days)`.
- Empty result sets skip sending mail.
- Failed SMTP sends log `MailSendResult.ErrorMessage`.
