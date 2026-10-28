#nullable enable
using System.Threading.Tasks;

namespace Backend;

public class MailData
{
    public string Receiver = string.Empty;

    public string Subject = string.Empty;

    public string Body = string.Empty;

    /// <summary>
    /// App-side pre-check (TFA / login flows): non-empty fields + allowed-domain email.
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
