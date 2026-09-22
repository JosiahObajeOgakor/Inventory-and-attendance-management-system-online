using Inventory.Application.Common;
using Inventory.Application.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Inventory.Infrastructure.Email;

/// <summary>Sends through Zoho Mail (or any SMTP server). Failures are turned into plain messages; the password never appears in a log line.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    private SmtpOptions Opt => options.Value;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Opt.User) && !string.IsNullOrWhiteSpace(Opt.Password);

    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        if (!IsConfigured) throw new BusinessRuleException("Email isn't set up yet: the server has no Zoho mailbox configured.");

        var mime = new MimeMessage();
        // Zoho only accepts mail sent as the authenticated account, so the address is fixed; the display name is the business.
        mime.From.Add(new MailboxAddress(m.FromName, string.IsNullOrWhiteSpace(Opt.FromAddress) ? Opt.User : Opt.FromAddress));
        mime.To.Add(new MailboxAddress(m.ToName ?? "", m.To));
        if (!string.IsNullOrWhiteSpace(m.ReplyTo) && MailboxAddress.TryParse(m.ReplyTo, out var reply)) mime.ReplyTo.Add(reply);
        mime.Subject = m.Subject;

        var b = new BodyBuilder { TextBody = m.Text };
        if (m.InlineLogo is { Length: > 0 })
        {
            var img = b.LinkedResources.Add("logo", m.InlineLogo, ContentType.Parse(LooksPng(m.InlineLogo) ? "image/png" : "image/jpeg"));
            img.ContentId = EmailTemplates.LogoCid;
        }
        b.HtmlBody = m.Html;
        foreach (var a in m.Attachments) b.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = b.ToMessageBody();

        try
        {
            using var smtp = new SmtpClient { Timeout = 30_000 };
            await smtp.ConnectAsync(Opt.Host, Opt.Port, Opt.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(Opt.User, Opt.Password, ct);
            await smtp.SendAsync(mime, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (AuthenticationException) { throw new BusinessRuleException("Zoho rejected the mailbox login. Check Smtp__User and Smtp__Password (an app-specific password if two-factor is on)."); }
        catch (SmtpCommandException ex) { log.LogWarning("SMTP refused a message: {Status}", ex.StatusCode); throw new BusinessRuleException("The mail server refused that message. Check the recipient's address."); }
        catch (Exception ex) when (ex is IOException or SmtpProtocolException or TimeoutException or System.Net.Sockets.SocketException)
        {
            log.LogWarning(ex, "SMTP connection failed");
            throw new BusinessRuleException("Couldn't reach the mail server. Check Smtp__Host and Smtp__Port, then try again.");
        }
    }

    private static bool LooksPng(byte[] d) => d.Length > 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
}
