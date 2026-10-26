#nullable enable
/// <summary>Outbound mail payload (standalone copy; no Godot dependencies).</summary>
public class MailData
{
    public string Receiver = string.Empty;

    public string Subject = string.Empty;

    public string Body = string.Empty;

    /// <summary>
    /// Pre-check: non-empty fields + allowed-domain email (<see cref="LoginValidation.Email"/>).
    /// SMTP send still validates independently inside <see cref="Mail.SendMail"/>.
    /// </summary>
    public bool Verify()
    {
        if (string.IsNullOrWhiteSpace(Receiver)
            || string.IsNullOrWhiteSpace(Subject)
            || string.IsNullOrWhiteSpace(Body))
        {
            return false;
        }

        return LoginValidation.Email(Receiver) is null;
    }

    public async Task<MailSendResult> SendAsync()
        => await Mail.SendMail(this);

    public async void Send()
    {
        await SendAsync();
    }
}
