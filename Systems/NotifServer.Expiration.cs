#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;


/// <summary>
/// Standalone (non-Godot) daily job: query Firestore for active SDS nearing / past expiration
/// and email a digest via <see cref="Mail"/>. Mailbox IMAP maintenance lives in a partial.
/// </summary>
public static partial class NotifServer
{
    private const string CollectionName = "sds";
    private const string DefaultConfigFileName = "expiration-settings.json";
    private const string DefaultTimeZone = "UTC";
    private const int DefaultRunHour = 12;
    private const int DefaultNotifyWindowDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<int> Main(string[] args)
    {
        bool once = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));
        bool help = args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase));

        if (help)
        {
            PrintHelp();
            return 0;
        }

        if (!TryResolveConfigPath(args, out string configPath, out string? configPathError))
        {
            Console.WriteLine($"Logging to: {GetCurrentLogFilePath()}");
            LogWarn($"Configuration error: {configPathError}");
            return 1;
        }

        if (!TryLoadSettings(configPath, out ServerSettings settings, out string? settingsError))
        {
            Console.WriteLine($"Logging to: {GetCurrentLogFilePath()}");
            LogWarn($"Configuration error: {settingsError}");
            return 1;
        }

        ConfigureLogDirectory(settings.LogDirectory);
        Console.WriteLine($"Logging to: {GetCurrentLogFilePath()}");
        LogInfo($"ChemClassify NotifServer starting (once={once}).");
        LogInfo($"Config file: {configPath}");
        LogSettings(settings);

        try
        {
            EnsureFirebase(settings.ProjectId);
        }
        catch (Exception ex)
        {
            LogWarn($"Firebase init failed: {ex.Message}");
            return 1;
        }

        if (once)
        {
            bool ok = await RunDigestAsync(settings);
            await RunMailboxMaintenanceAsync(settings);
            return ok ? 0 : 2;
        }

        LogInfo($"Daemon mode — waiting for daily run at {settings.RunHour:00}:00 ({settings.TimeZone.Id}).");
        LogInfo($"Press Ctrl+C to stop. Edit {DefaultConfigFileName} to change schedule/recipients without restart.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            if (!TryLoadSettings(configPath, out settings, out settingsError))
            {
                LogWarn($"Configuration error (will retry in 5 minutes): {settingsError}");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            ConfigureLogDirectory(settings.LogDirectory);

            TimeSpan delay = DelayUntilNextRun(settings.TimeZone, settings.RunHour);
            LogInfo($"Next digest in {FormatDelay(delay)} (run {settings.RunHour:00}:00 {settings.TimeZone.Id}).");

            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!TryLoadSettings(configPath, out settings, out settingsError))
            {
                LogWarn($"Configuration error before digest — skipping this run: {settingsError}");
                continue;
            }

            ConfigureLogDirectory(settings.LogDirectory);
            LogSettings(settings);
            await RunDigestAsync(settings);
            await RunMailboxMaintenanceAsync(settings);
        }

        LogInfo("Shutdown.");
        return 0;
    }

    private static async Task<bool> RunDigestAsync(ServerSettings settings)
    {
        LogInfo("Running expiration digest…");

        try
        {
            FirestoreDb db = FirestoreDb.Create(settings.ProjectId);
            DateTime thresholdUtc = DateTime.UtcNow.Date.AddDays(settings.NotifyWindowDays + 1);

            Query query = db.Collection(CollectionName)
                .WhereEqualTo("active", true)
                .WhereLessThanOrEqualTo("expiresAt", Timestamp.FromDateTime(DateTime.SpecifyKind(thresholdUtc, DateTimeKind.Utc)));

            QuerySnapshot snap = await query.GetSnapshotAsync();
            List<ExpiringSds> rows = new();

            foreach (DocumentSnapshot doc in snap.Documents)
            {
                if (TryParse(doc, out ExpiringSds row))
                    rows.Add(row);
            }

            rows.Sort((a, b) => a.ExpiresAt.CompareTo(b.ExpiresAt));

            DateTime todayUtc = DateTime.UtcNow.Date;
            int expired = rows.Count(r => r.ExpiresAt.Date < todayUtc);
            int expiring = rows.Count - expired;

            LogInfo(
                $"Firestore returned {rows.Count} active SDS with expiresAt <= {thresholdUtc:yyyy-MM-dd} UTC "
                + $"({expired} expired, {expiring} within {settings.NotifyWindowDays} days).");

            if (rows.Count == 0)
            {
                LogInfo("Nothing to notify — skipping mail.");
                return true;
            }

            // One batched digest body for all SDS; one send per recipient (not per SDS).
            string subject =
                $"[ChemClassify] {rows.Count} SDS expiration alert(s) "
                + $"({expired} expired / {expiring} within {settings.NotifyWindowDays}d)";

            string body = BuildBody(rows, settings.NotifyWindowDays, todayUtc);
            int sent = 0;
            int failed = 0;

            foreach (string recipient in settings.AlertRecipients)
            {
                var mail = new MailData
                {
                    Receiver = recipient,
                    Subject = subject,
                    Body = body
                };

                MailSendResult result = await Mail.SendMail(mail);
                if (result.Ok)
                {
                    sent++;
                    LogInfo($"Mail sent to {recipient}.");
                }
                else
                {
                    failed++;
                    LogWarn($"Mail failed for {recipient}: {result.ErrorMessage}");
                }
            }

            LogInfo($"Digest finished (sent={sent}, failed={failed}).");
            return failed == 0;
        }
        catch (Exception ex)
        {
            LogWarn($"Digest failed: {ex.Message}");
            return false;
        }
    }

    private static string BuildBody(IReadOnlyList<ExpiringSds> rows, int windowDays, DateTime todayUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ChemClassify SDS expiration digest");
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Notify window: {windowDays} day(s)");
        sb.AppendLine();

        sb.AppendLine("--- Already expired ---");
        AppendSection(sb, rows.Where(r => r.ExpiresAt.Date < todayUtc).ToList(), todayUtc);
        sb.AppendLine();
        sb.AppendLine($"--- Expiring within {windowDays} day(s) ---");
        AppendSection(sb, rows.Where(r => r.ExpiresAt.Date >= todayUtc).ToList(), todayUtc);
        sb.AppendLine();
        sb.AppendLine("Open the ChemClassify desktop app → chemicals library for details / PDF links.");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, IReadOnlyList<ExpiringSds> rows, DateTime todayUtc)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (ExpiringSds row in rows)
        {
            int days = (row.ExpiresAt.Date - todayUtc).Days;
            string relative = days < 0 ? $"{Math.Abs(days)}d overdue" : days == 0 ? "expires today" : $"in {days}d";
            sb.AppendLine(
                $"- {row.DisplayName} | id={row.SdsId} | expires={row.ExpiresAt:yyyy-MM-dd} ({relative})"
                + (string.IsNullOrWhiteSpace(row.CasNumber) ? "" : $" | CAS={row.CasNumber}"));
        }
    }

    private static bool TryParse(DocumentSnapshot doc, out ExpiringSds row)
    {
        row = default;
        if (!doc.Exists)
            return false;

        Dictionary<string, object> data = doc.ToDictionary();
        string sdsId = GetString(data, "sdsId") ?? doc.Id;
        string? displayName = GetString(data, "displayName");
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        DateTime? expires = GetTimestampUtc(data, "expiresAt");
        if (expires is null)
            return false;

        row = new ExpiringSds(sdsId, displayName, expires.Value, GetString(data, "casNumber"));
        return true;
    }

    private static string? GetString(Dictionary<string, object> data, string key)
        => data.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static DateTime? GetTimestampUtc(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out object? value) || value is null)
            return null;
        if (value is Timestamp ts)
            return ts.ToDateTime();
        if (value is DateTime dt)
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return null;
    }

    private static void EnsureFirebase(string projectId)
    {
        if (FirebaseApp.DefaultInstance != null)
            return;

        string? credPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (string.IsNullOrWhiteSpace(credPath) || !File.Exists(credPath))
        {
            throw new InvalidOperationException(
                "GOOGLE_APPLICATION_CREDENTIALS must point to an existing service-account JSON file.");
        }

        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.GetApplicationDefault(),
            ProjectId = projectId
        });
    }

    private static bool TryResolveConfigPath(string[] args, out string configPath, out string? error)
    {
        configPath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                error = "--config requires a file path.";
                return false;
            }

            configPath = Path.GetFullPath(args[i + 1]);
            return true;
        }

        return true;
    }

    private static bool TryLoadSettings(string configPath, out ServerSettings settings, out string? error)
    {
        settings = default;
        error = null;

        string? projectId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID"),
            Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"),
            Environment.GetEnvironmentVariable("GCLOUD_PROJECT"),
            FirebaseApp.DefaultInstance?.Options?.ProjectId);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            error = "Set FIREBASE_PROJECT_ID (or GOOGLE_CLOUD_PROJECT / GCLOUD_PROJECT).";
            return false;
        }

        if (!TryLoadUserSettings(configPath, out UserSettingsFile user, out error))
            return false;

        settings = new ServerSettings(
            projectId,
            user.Recipients,
            user.TimeZone,
            user.RunHour,
            user.NotifyWindowDays,
            user.LogDirectory);
        return true;
    }

    private static bool TryLoadUserSettings(string configPath, out UserSettingsFile user, out string? error)
    {
        user = default;
        error = null;

        if (!File.Exists(configPath))
        {
            error =
                $"Missing config file '{configPath}'. Copy expiration-settings.example.json to "
                + $"{DefaultConfigFileName} next to the exe (or pass --config <path>).";
            return false;
        }

        ExpirationSettingsDto? dto;
        try
        {
            string json = File.ReadAllText(configPath);
            dto = JsonSerializer.Deserialize<ExpirationSettingsDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON in '{configPath}': {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = $"Could not read '{configPath}': {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = $"Config file '{configPath}' deserialized to null.";
            return false;
        }

        string tzId = string.IsNullOrWhiteSpace(dto.TimeZone) ? DefaultTimeZone : dto.TimeZone.Trim();
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            error =
                $"Unknown timeZone '{tzId}'. Use a Windows id (e.g. \"SA Pacific Standard Time\") "
                + "or IANA id on Linux (e.g. \"America/Bogota\").";
            return false;
        }
        catch (InvalidTimeZoneException ex)
        {
            error = $"Invalid timeZone '{tzId}': {ex.Message}";
            return false;
        }

        int runHour = dto.RunHour ?? DefaultRunHour;
        if (runHour is < 0 or > 23)
        {
            error = "runHour must be an integer 0–23 (local hour in timeZone).";
            return false;
        }

        int windowDays = dto.NotifyWindowDays ?? DefaultNotifyWindowDays;
        if (windowDays is < 1 or > 365)
        {
            error = "notifyWindowDays must be an integer 1–365.";
            return false;
        }

        if (dto.Recipients is null || dto.Recipients.Count == 0)
        {
            error = "recipients must be a non-empty JSON array of email addresses.";
            return false;
        }

        List<string> recipients = dto.Recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            error = "recipients did not contain any email addresses.";
            return false;
        }

        string logDirectory = string.IsNullOrWhiteSpace(dto.LogDirectory)
            ? DefaultLogDirectoryName
            : dto.LogDirectory.Trim();

        user = new UserSettingsFile(recipients, tz, runHour, windowDays, logDirectory);
        return true;
    }

    private static void LogSettings(ServerSettings settings)
    {
        LogInfo($"Project:     {settings.ProjectId}");
        LogInfo($"Recipients:  {string.Join(", ", settings.AlertRecipients)}");
        LogInfo($"Timezone:    {settings.TimeZone.Id}");
        LogInfo($"Run hour:    {settings.RunHour:00}:00 (local)");
        LogInfo($"Window days: {settings.NotifyWindowDays}");
        LogInfo($"Log dir:     {ResolveLogDirectory(settings.LogDirectory)}");
    }

    /// <summary>Delay until the next local <paramref name="runHour"/>:00 in <paramref name="tz"/>.</summary>
    internal static TimeSpan DelayUntilNextRun(TimeZoneInfo tz, int runHour)
    {
        DateTime utcNow = DateTime.UtcNow;
        DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        DateTime localTarget = new(localNow.Year, localNow.Month, localNow.Day, runHour, 0, 0, DateTimeKind.Unspecified);

        if (localNow >= localTarget)
            localTarget = localTarget.AddDays(1);

        DateTime utcTarget = TimeZoneInfo.ConvertTimeToUtc(localTarget, tz);
        TimeSpan delay = utcTarget - utcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay.TotalHours >= 24)
            return $"{(int)delay.TotalDays}d {delay.Hours}h {delay.Minutes}m";
        if (delay.TotalHours >= 1)
            return $"{(int)delay.TotalHours}h {delay.Minutes}m";
        return $"{delay.Minutes}m {delay.Seconds}s";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            ChemClassify NotifServer — daily SDS expiration digest (no Godot).

            Usage:
              ExpirationNotifServer                    Run as daemon (wait until runHour, then every day)
              ExpirationNotifServer --once             Run one digest now and exit
              ExpirationNotifServer --config <path>    Use a settings file other than ./expiration-settings.json
              ExpirationNotifServer --help             Show this help

            Admin settings (JSON file next to the exe, or --config):
              expiration-settings.json
                timeZone           Time zone id (default UTC). Examples:
                                   Windows: "SA Pacific Standard Time"
                                   Linux:   "America/Bogota"
                runHour            Local hour 0–23 (default 12)
                notifyWindowDays   Days ahead to include (default 30)
                recipients         JSON array of alert emails (required)
                logDirectory       Folder for monthly TXT logs (default "logs").
                                   Relative paths are under the exe directory; absolute OK.

              Copy expiration-settings.example.json → expiration-settings.json and edit.
              Daemon reloads the file each cycle (no restart needed for schedule/recipients/log dir).

            Mail behavior:
              One batched digest listing all matching SDS; one email per recipient (not per SDS).
              Zero matches → no mail sent.

            Mailbox maintenance (after each digest cycle, via IMAP on SMTP_HOST):
              Prune Sent messages older than 1 day. Never delete Inbox.
              If Inbox has any mail, log a warning and email recipients (private SMTP account).

            Logging:
              Operational logs go to <logDirectory>/expiration-notif-YYYY-MM.txt (UTC month).
              Console only prints the log file path at startup (and this --help text).

            Required environment (secrets / infra):
              GOOGLE_APPLICATION_CREDENTIALS   Service account JSON path
              FIREBASE_PROJECT_ID              Firestore project id
              SMTP_HOST / SMTP_PORT / SMTP_USER / SMTP_PASS / SMTP_FROM
              IMAP_PORT                        IMAP port on SMTP_HOST (default 993, like SMTP_PORT)

            Optional environment:
              ALLOWED_DOMAIN                   Used by MailData.Verify (optional for digest send)

            Autostart (Windows VM): see DevManual.Setup.md
            """);
    }

    private sealed class ExpirationSettingsDto
    {
        [JsonPropertyName("timeZone")]
        public string? TimeZone { get; set; }

        [JsonPropertyName("runHour")]
        public int? RunHour { get; set; }

        [JsonPropertyName("notifyWindowDays")]
        public int? NotifyWindowDays { get; set; }

        [JsonPropertyName("recipients")]
        public List<string>? Recipients { get; set; }

        [JsonPropertyName("logDirectory")]
        public string? LogDirectory { get; set; }
    }

    private readonly record struct UserSettingsFile(
        IReadOnlyList<string> Recipients,
        TimeZoneInfo TimeZone,
        int RunHour,
        int NotifyWindowDays,
        string LogDirectory);

    private readonly record struct ServerSettings(
        string ProjectId,
        IReadOnlyList<string> AlertRecipients,
        TimeZoneInfo TimeZone,
        int RunHour,
        int NotifyWindowDays,
        string LogDirectory);

    private readonly record struct ExpiringSds(
        string SdsId,
        string DisplayName,
        DateTime ExpiresAt,
        string? CasNumber);
}
