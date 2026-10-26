#nullable enable
using System.Net.Mail;
using MimeKit;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;


/// <summary>SMTP helper (standalone copy; no Godot dependencies).</summary>
public static class Mail
{
    public static string? Host = Environment.GetEnvironmentVariable("SMTP_HOST");
    public static int Port = int.Parse(Environment.GetEnvironmentVariable("SMTP_PORT") ?? "587");
    public static string? User = Environment.GetEnvironmentVariable("SMTP_USER");
    public static string? Pass = Environment.GetEnvironmentVariable("SMTP_PASS");
    public static string? From = Environment.GetEnvironmentVariable("SMTP_FROM");

    /// <summary>
    /// Sends <paramref name="sendableMail"/> over SMTP. Returns a result on validation or transport
    /// failure instead of throwing for expected misconfiguration / server errors.
    /// </summary>
    public static async Task<MailSendResult> SendMail(MailData sendableMail)
    {
        if (ValidateConfiguration() is string configError)
            return MailSendResult.Fail(configError);

        if (ValidateMailData(sendableMail) is string dataError)
            return MailSendResult.Fail(dataError);

        string smtpHost = Host!;
        string smtpUser = User!;
        string smtpPass = Pass!;
        string smtpFrom = From!;

        try
        {
            var mailMessage = new MimeMessage();
            mailMessage.From.Add(MailboxAddress.Parse(smtpFrom));
            mailMessage.To.Add(MailboxAddress.Parse(sendableMail.Receiver));
            mailMessage.Subject = sendableMail.Subject;
            mailMessage.Body = new TextPart("plain") { Text = sendableMail.Body };

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpHost, Port, MailKit.Security.SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(smtpUser, smtpPass);
            await client.SendAsync(mailMessage);
            await client.DisconnectAsync(true);
            return MailSendResult.Success();
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            return MailSendResult.Fail($"SMTP authentication failed: {ex.Message}");
        }
        catch (MailKit.Net.Smtp.SmtpCommandException ex)
        {
            return MailSendResult.Fail($"SMTP rejected the message ({ex.StatusCode}): {ex.Message}");
        }
        catch (MailKit.Net.Smtp.SmtpProtocolException ex)
        {
            return MailSendResult.Fail($"SMTP protocol error: {ex.Message}");
        }
        catch (FormatException ex)
        {
            return MailSendResult.Fail($"Invalid email address format: {ex.Message}");
        }
        catch (Exception ex)
        {
            return MailSendResult.Fail($"Could not send mail: {ex.Message}");
        }
    }

    private static string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Host))
            return "SMTP_HOST is not set.";
        if (Port is < 1 or > 65535)
            return "SMTP_PORT must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(User))
            return "SMTP_USER is not set.";
        if (string.IsNullOrWhiteSpace(Pass))
            return "SMTP_PASS is not set.";
        if (string.IsNullOrWhiteSpace(From))
            return "SMTP_FROM is not set.";

        try
        {
            _ = new MailAddress(From);
        }
        catch (FormatException)
        {
            return "SMTP_FROM is not a valid email address.";
        }

        return null;
    }

    private static string? ValidateMailData(MailData mail)
    {
        if (mail is null)
            return "Mail data is required.";
        if (string.IsNullOrWhiteSpace(mail.Receiver))
            return "Mail receiver is required.";
        if (string.IsNullOrWhiteSpace(mail.Subject))
            return "Mail subject is required.";
        if (string.IsNullOrWhiteSpace(mail.Body))
            return "Mail body is required.";

        try
        {
            _ = new MailAddress(mail.Receiver.Trim());
        }
        catch (FormatException)
        {
            return "Mail receiver is not a valid email address.";
        }

        return null;
    }
}

/// <summary>Outcome of <see cref="Mail.SendMail"/> for job logging.</summary>
public readonly record struct MailSendResult(bool Ok, string? ErrorMessage)
{
    public static MailSendResult Success() => new(true, null);

    public static MailSendResult Fail(string message) => new(false, message);
}
