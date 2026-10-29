#nullable enable
using System.Text;
using Backend;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;


/// <summary>
/// IMAP mailbox hygiene for the private SMTP sender account: prune old Sent mail,
/// warn admins if Inbox is non-empty (never deletes Inbox).
/// </summary>
public static partial class NotifServer
{
    private static readonly TimeSpan SentRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// Soft-fail mailbox maintenance after an alert cycle. Failures are logged only;
    /// they do not change the alert exit code.
    /// </summary>
    private static async Task RunMailboxMaintenanceAsync(ServerSettings settings)
    {
        LogInfo("Running IMAP mailbox maintenance…");

        try
        {
            if (!TryResolveImapSettings(out string host, out int port, out string user, out string pass, out string? error))
            {
                LogWarn($"Mailbox maintenance skipped: {error}");
                return;
            }

            using var client = new ImapClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(user, pass);

            await PruneOldSentAsync(client);
            await WarnIfInboxNonEmptyAsync(client, settings);

            await client.DisconnectAsync(true);
            LogInfo("Mailbox maintenance finished.");
        }
        catch (Exception ex)
        {
            LogWarn($"Mailbox maintenance failed: {ex.Message}");
        }
    }

    private static bool TryResolveImapSettings(
        out string host,
        out int port,
        out string user,
        out string pass,
        out string? error)
    {
        host = Environment.GetEnvironmentVariable("SMTP_HOST")?.Trim() ?? "";
        user = Environment.GetEnvironmentVariable("SMTP_USER")?.Trim() ?? "";
        pass = Environment.GetEnvironmentVariable("SMTP_PASS") ?? "";
        error = null;

        if (!int.TryParse(Environment.GetEnvironmentVariable("IMAP_PORT") ?? "993", out port)
            || port is < 1 or > 65535)
        {
            error = "IMAP_PORT must be an integer 1–65535 (default 993).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "SMTP_HOST is not set (IMAP uses the same host).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            error = "SMTP_USER / SMTP_PASS are required for IMAP.";
            return false;
        }

        return true;
    }

    private static async Task PruneOldSentAsync(ImapClient client)
    {
        IMailFolder? sent;
        try
        {
            sent = client.GetFolder(SpecialFolder.Sent);
        }
        catch (FolderNotFoundException)
        {
            LogInfo("Sent special folder not available — skipping Sent prune.");
            return;
        }

        if (sent is null)
        {
            LogInfo("Sent special folder not available — skipping Sent prune.");
            return;
        }

        await sent.OpenAsync(FolderAccess.ReadWrite);

        DateTime cutoffUtc = DateTime.UtcNow - SentRetention;
        // IMAP BEFORE uses delivery/internal date; delete anything older than retention.
        IList<UniqueId> stale = await sent.SearchAsync(SearchQuery.DeliveredBefore(cutoffUtc));

        if (stale.Count == 0)
        {
            LogInfo($"Sent prune: nothing older than {SentRetention.TotalDays:0} day(s).");
            await sent.CloseAsync();
            return;
        }

        await sent.AddFlagsAsync(stale, MessageFlags.Deleted, true);
        await sent.ExpungeAsync();
        await sent.CloseAsync();

        LogInfo($"Sent prune: deleted {stale.Count} message(s) older than {SentRetention.TotalDays:0} day(s).");
    }

    private static async Task WarnIfInboxNonEmptyAsync(ImapClient client, ServerSettings settings)
    {
        IMailFolder inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        int count = inbox.Count;
        await inbox.CloseAsync();

        if (count <= 0)
        {
            LogInfo("Inbox check: empty (OK for private SMTP account).");
            return;
        }

        string account = Environment.GetEnvironmentVariable("SMTP_USER")?.Trim() ?? "(unknown)";
        LogWarn(
            $"WARNING: private SMTP Inbox for '{account}' contains {count} message(s). "
            + "Inbox is never auto-deleted — please review the mailbox.");

        string subject = $"[ChemClassify] WARNING: {count} message(s) in SMTP account Inbox";
        var body = new StringBuilder();
        body.AppendLine("ChemClassify NotifServer — Inbox warning");
        body.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        body.AppendLine();
        body.AppendLine($"The private SMTP/IMAP account '{account}' has {count} message(s) in Inbox.");
        body.AppendLine("This daemon never deletes Inbox mail (only Sent older than 1 day).");
        body.AppendLine("Please sign in to the mailbox and clear or relocate those messages.");
        body.AppendLine();
        body.AppendLine("(Message contents are intentionally not included in this alert.)");

        string bodyText = body.ToString();
        foreach (string recipient in settings.AlertRecipients)
        {
            MailSendResult result = await Mail.SendMail(new MailData
            {
                Receiver = recipient,
                Subject = subject,
                Body = bodyText
            });

            if (result.Ok)
                LogInfo($"Inbox warning mailed to {recipient}.");
            else
                LogWarn($"Inbox warning mail failed for {recipient}: {result.ErrorMessage}");
        }
    }
}
