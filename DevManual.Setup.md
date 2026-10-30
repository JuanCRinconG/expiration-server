# Expiration notification server — setup

Standalone **.NET 8** console repo (not part of ChemClassify / Godot). Once per day at a configurable local hour (default **12:00**), it loads **`--SYSTEM--/alert`**, evaluates configured **rules** against **sources** (partitioned inventory paths such as `--DATA--/default/items`), then emails a **batched alert** (one mail per recipient) via the local `Mail` / `MailData` copies under namespace `ExpirationServer`.

## Layout

| File | Role |
| --- | --- |
| `Systems/NotifServer.Expiration.cs` | Entry point / daily daemon / settings (partial `NotifServer`) |
| `Systems/NotifServer.Alert.cs` | Rule evaluation and sectioned alert mail (partial `NotifServer`) |
| `Systems/NotifServer.MailCleanup.cs` | IMAP Sent prune + Inbox admin warning (partial) |
| `Systems/NotifServer.Logging.cs` | Monthly TXT file logging (partial) |
| `Systems/Mail.cs` | SMTP send + `MailSendResult` (env read per send) |
| `Systems/MailData.cs` | Message payload |
| `Systems/LoginValidation.cs` | Email domain check used by `MailData.Verify` |
| `expiration-settings.example.json` | Template for admin settings (copy → `expiration-settings.json`) |
| `Register-Autostart.ps1` | Windows Task Scheduler at logon |
| `ExpirationServer.csproj` | Console project (exe name `ExpirationNotifServer`) |
| `Backend/` Firebase read files | Pasted from chem-classify by `SyncProjects` (`DatabaseTopology`, `FirebaseDataTools`, and the other read sources). Committed in this repo. |

Server-owned types live in the global namespace (`NotifServer`). Pasted read types and this repo’s `BackendServices`, `Mail`, and `BackendLog` live in namespace **`Backend`**.

## Prerequisites

### Environment (secrets / infra)

Apply User env vars once via `chem-classify`’s `DeveloperTools/ConfigureEnvironments/ConfigureEnvironments.ps1` + `env.secrets.json` (full disk paths as top-level keys; fire-and-forget — see that repo’s Quick setup). Prefer **At log on** Task Scheduler so those User variables are available.

| Variable | Required | Purpose |
| --- | --- | --- |
| `GOOGLE_APPLICATION_CREDENTIALS` | yes | Service account JSON path |
| `FIREBASE_PROJECT_ID` | yes | Firestore project id (`GOOGLE_CLOUD_PROJECT` / `GCLOUD_PROJECT` also work) |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASS` / `SMTP_FROM` | yes | SMTP (IMAP uses the same host + user/pass) |
| `IMAP_PORT` | no | IMAP port on `SMTP_HOST` (default `993`; used only by `NotifServer.MailCleanup`) |
| `ALLOWED_DOMAIN` | no | Only if you call `MailData.Verify` |

Firestore: seed **`--SYSTEM--/alert`** (rules + sources) via desktop **Notification setup**. Per-source composite indexes may be required when Firestore prompts (typically `active` on each inventory collection).

### Admin settings (JSON file)

Place `expiration-settings.json` next to the published exe (or pass `--config <path>`). Copy from the shipped example:

```powershell
Copy-Item .\expiration-settings.example.json .\expiration-settings.json
# After publish:
Copy-Item .\publish\expiration-settings.example.json .\publish\expiration-settings.json
```

Example:

```json
{
  "timeZone": "SA Pacific Standard Time",
  "runHour": 12,
  "logDirectory": "logs",
  "recipients": [
    "lab-manager@universidad.edu"
  ]
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `timeZone` | no (default `UTC`) | Windows e.g. `SA Pacific Standard Time`; Linux e.g. `America/Bogota` |
| `runHour` | no (default `12`) | Local hour `0`–`23` in `timeZone` |
| `logDirectory` | no (default `logs`) | Folder for monthly TXT logs; relative paths are under the exe directory |
| `recipients` | yes | Non-empty JSON array of emails |

Date-rule **lead days** live on each rule in Firestore (`--SYSTEM--/alert`), configured from desktop **Notification setup**.

The daemon **reloads** this file each cycle, so schedule and recipients can change without restarting.

### Time zone examples

| OS | Example id |
| --- | --- |
| Windows (Colombia) | `SA Pacific Standard Time` |
| Linux / macOS | `America/Bogota` |
| UTC | `UTC` |

## Build / run

```powershell
cd <this-repo>
Copy-Item .\expiration-settings.example.json .\expiration-settings.json
# Edit recipients / timezone / window as needed

dotnet build .\ExpirationServer.csproj -c Release

# One-shot test
dotnet run --project .\ExpirationServer.csproj -c Release -- --once

# Daemon
dotnet run --project .\ExpirationServer.csproj -c Release

# Alternate config path
dotnet run --project .\ExpirationServer.csproj -c Release -- --config D:\path\to\expiration-settings.json --once
```

Publish:

```powershell
dotnet publish .\ExpirationServer.csproj -c Release -o .\publish
Copy-Item .\publish\expiration-settings.example.json .\publish\expiration-settings.json
# Edit .\publish\expiration-settings.json
.\publish\ExpirationNotifServer.exe --once
```

## Autostart (Windows VM)

Prefer **At log on** so User environment variables (Firebase / SMTP credentials) are available. Admin schedule/recipients live in `expiration-settings.json` next to the exe.

```powershell
.\Register-Autostart.ps1
```

Or Task Scheduler manually: trigger At log on → start `ExpirationNotifServer.exe` (no `--once`).

Remove:

```powershell
Unregister-ScheduledTask -TaskName "ChemClassifyExpirationNotif" -Confirm:$false
```

## Logging

Operational output is written to monthly TXT files under `logDirectory` from JSON (default `logs` next to the exe):

```text
<logDirectory>/expiration-notif-YYYY-MM.txt
```

- One file per **UTC** calendar month; daily runs append to the current month’s file.
- At month change a new file is created automatically; old months are kept.
- Relative `logDirectory` values are resolved from the exe folder; absolute paths are allowed.
- Changing `logDirectory` in JSON is picked up on the next daemon reload cycle.
- Console only prints the current log path at startup (plus `--help`).

## Mailbox maintenance (IMAP)

After each alert cycle (including `--once`, even when no SDS mail was sent):

1. Connect IMAP to **`SMTP_HOST`:`IMAP_PORT`** with `SMTP_USER` / `SMTP_PASS`.
2. **Sent:** delete messages older than **1 day** (never invents folder names — uses the server’s Sent special folder).
3. **Inbox:** never deleted. If Inbox has any messages, the daemon logs a warning and emails the JSON `recipients` (no message bodies included).

Mailbox maintenance failures are logged only; they do not override the alert exit code.

## Notes

- Query: `active == true` AND `expiresAt <= (UTC today + window days)`.
- Empty result sets skip sending mail.
- Matching SDS are batched into **one alert body**; that alert is sent once per `recipients` entry (not one mail per SDS).
- Failed SMTP sends log `MailSendResult.ErrorMessage`.
