#nullable enable
using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;


/// <summary>
/// Standalone (non-Godot) daily job: query Firestore for active SDS nearing / past expiration
/// and email a digest via <see cref="Mail"/>.
/// </summary>
public static class ExpirationNotifServer
{
    private const string CollectionName = "sds";
    private const string DefaultTimeZone = "UTC";
    private const int DefaultRunHour = 12;
    private const int DefaultNotifyWindowDays = 30;

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

        Console.WriteLine($"[{Stamp()}] ChemClassify ExpirationNotifServer starting (once={once}).");

        if (!TryLoadSettings(out ServerSettings settings, out string? settingsError))
        {
            Console.Error.WriteLine($"[{Stamp()}] Configuration error: {settingsError}");
            return 1;
        }

        LogSettings(settings);

        try
        {
            EnsureFirebase(settings.ProjectId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Stamp()}] Firebase init failed: {ex.Message}");
            return 1;
        }

        if (once)
        {
            bool ok = await RunDigestAsync(settings);
            return ok ? 0 : 2;
        }

        Console.WriteLine($"[{Stamp()}] Daemon mode — waiting for daily run at {settings.RunHour:00}:00 ({settings.TimeZone.Id}).");
        Console.WriteLine($"[{Stamp()}] Press Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            TimeSpan delay = DelayUntilNextRun(settings.TimeZone, settings.RunHour);
            Console.WriteLine($"[{Stamp()}] Next digest in {FormatDelay(delay)}.");

            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunDigestAsync(settings);
        }

        Console.WriteLine($"[{Stamp()}] Shutdown.");
        return 0;
    }

    private static async Task<bool> RunDigestAsync(ServerSettings settings)
    {
        Console.WriteLine($"[{Stamp()}] Running expiration digest…");

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

            Console.WriteLine(
                $"[{Stamp()}] Firestore returned {rows.Count} active SDS with expiresAt <= {thresholdUtc:yyyy-MM-dd} UTC "
                + $"({expired} expired, {expiring} within {settings.NotifyWindowDays} days).");

            if (rows.Count == 0)
            {
                Console.WriteLine($"[{Stamp()}] Nothing to notify — skipping mail.");
                return true;
            }

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
                    Console.WriteLine($"[{Stamp()}] Mail sent to {recipient}.");
                }
                else
                {
                    failed++;
                    Console.Error.WriteLine($"[{Stamp()}] Mail failed for {recipient}: {result.ErrorMessage}");
                }
            }

            Console.WriteLine($"[{Stamp()}] Digest finished (sent={sent}, failed={failed}).");
            return failed == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Stamp()}] Digest failed: {ex.Message}");
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

    private static bool TryLoadSettings(out ServerSettings settings, out string? error)
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

        string? recipientsRaw = Environment.GetEnvironmentVariable("EXPIRATION_ALERT_TO");
        if (string.IsNullOrWhiteSpace(recipientsRaw))
        {
            error = "Set EXPIRATION_ALERT_TO to one or more recipient emails (comma-separated).";
            return false;
        }

        List<string> recipients = recipientsRaw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            error = "EXPIRATION_ALERT_TO did not contain any email addresses.";
            return false;
        }

        string tzId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("EXPIRATION_TIMEZONE"),
            Environment.GetEnvironmentVariable("TZ"),
            DefaultTimeZone)!;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            error =
                $"Unknown timezone '{tzId}'. Use a Windows id (e.g. \"SA Pacific Standard Time\") "
                + "or IANA id on Linux (e.g. \"America/Bogota\").";
            return false;
        }
        catch (InvalidTimeZoneException ex)
        {
            error = $"Invalid timezone '{tzId}': {ex.Message}";
            return false;
        }

        int runHour = DefaultRunHour;
        string? hourRaw = Environment.GetEnvironmentVariable("EXPIRATION_RUN_HOUR");
        if (!string.IsNullOrWhiteSpace(hourRaw))
        {
            if (!int.TryParse(hourRaw, out runHour) || runHour is < 0 or > 23)
            {
                error = "EXPIRATION_RUN_HOUR must be an integer 0–23 (local hour in EXPIRATION_TIMEZONE).";
                return false;
            }
        }

        int windowDays = DefaultNotifyWindowDays;
        string? windowRaw = Environment.GetEnvironmentVariable("EXPIRATION_NOTIFY_WINDOW_DAYS");
        if (!string.IsNullOrWhiteSpace(windowRaw))
        {
            if (!int.TryParse(windowRaw, out windowDays) || windowDays is < 1 or > 365)
            {
                error = "EXPIRATION_NOTIFY_WINDOW_DAYS must be an integer 1–365.";
                return false;
            }
        }

        settings = new ServerSettings(projectId, recipients, tz, runHour, windowDays);
        return true;
    }

    private static void LogSettings(ServerSettings settings)
    {
        Console.WriteLine($"[{Stamp()}] Project:     {settings.ProjectId}");
        Console.WriteLine($"[{Stamp()}] Recipients:  {string.Join(", ", settings.AlertRecipients)}");
        Console.WriteLine($"[{Stamp()}] Timezone:    {settings.TimeZone.Id}");
        Console.WriteLine($"[{Stamp()}] Run hour:    {settings.RunHour:00}:00 (local)");
        Console.WriteLine($"[{Stamp()}] Window days: {settings.NotifyWindowDays}");
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
            ChemClassify ExpirationNotifServer — daily SDS expiration digest (no Godot).

            Usage:
              ExpirationNotifServer              Run as daemon (wait until midday, then every day)
              ExpirationNotifServer --once       Run one digest now and exit
              ExpirationNotifServer --help       Show this help

            Required environment:
              GOOGLE_APPLICATION_CREDENTIALS   Service account JSON path
              FIREBASE_PROJECT_ID              Firestore project id
              EXPIRATION_ALERT_TO              Recipient emails (comma-separated)
              SMTP_HOST / SMTP_PORT / SMTP_USER / SMTP_PASS / SMTP_FROM

            Optional environment:
              EXPIRATION_TIMEZONE              Time zone id (default UTC). Examples:
                                               Windows: "SA Pacific Standard Time"
                                               Linux:   "America/Bogota"
              EXPIRATION_RUN_HOUR              Local hour 0–23 (default 12 = midday)
              EXPIRATION_NOTIFY_WINDOW_DAYS    Days ahead to include (default 30)
              ALLOWED_DOMAIN                   Used by MailData.Verify (optional for digest send)

            Autostart (Windows VM): see DevManual.Setup.md
            """);
    }

    private readonly record struct ServerSettings(
        string ProjectId,
        IReadOnlyList<string> AlertRecipients,
        TimeZoneInfo TimeZone,
        int RunHour,
        int NotifyWindowDays);

    private readonly record struct ExpiringSds(
        string SdsId,
        string DisplayName,
        DateTime ExpiresAt,
        string? CasNumber);
}
