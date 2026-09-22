namespace Inventory.Application.Email;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

/// <param name="InlineLogo">Shown in the header through a Content-ID, so it appears without the recipient having to "load images".</param>
public sealed record EmailMessage(string To, string? ToName, string Subject, string Html, string Text, IReadOnlyList<EmailAttachment> Attachments,
    string FromName, string? ReplyTo, byte[]? InlineLogo = null);

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Zoho Mail over SMTP. The password comes from the environment (Smtp__Password), never from a file in the repository.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    /// <summary>smtp.zoho.com for personal accounts, smtppro.zoho.com for Zoho Workplace / custom domains; .eu, .in etc. depend on the data centre.</summary>
    public string Host { get; set; } = "smtppro.zoho.com";
    public int Port { get; set; } = 465;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Zoho only lets you send as the account (or a verified alias) — so this normally equals User.</summary>
    public string FromAddress { get; set; } = "";
    public int MaxPerUserPerHour { get; set; } = 30;
}
