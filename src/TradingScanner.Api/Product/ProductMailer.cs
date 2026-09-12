using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace TradingScanner.Api.Product;

public sealed class MailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed class ProductMailer(IOptions<MailOptions> options, ILogger<ProductMailer> logger)
{
    public bool Configured => options.Value.Configured;

    public async Task<bool> SendLinkAsync(string email, string subject, string instruction, string url)
    {
        var o = options.Value;
        if (!o.Configured) return false;
        using var message = new MailMessage(o.From, email, subject,
            $"{HtmlEncoder.Default.Encode(instruction)}<br><a href=\"{HtmlEncoder.Default.Encode(url)}\">Continue securely</a><p>This link expires in one hour. If you did not request this, ignore it.</p>") { IsBodyHtml = true };
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl, Timeout = 15000 };
        if (!string.IsNullOrEmpty(o.Username)) client.Credentials = new NetworkCredential(o.Username, o.Password);
        try { await client.SendMailAsync(message); return true; }
        catch (SmtpException ex)
        {
            // Never log the recovery token, recipient or SMTP credentials.
            logger.LogWarning("Account email delivery failed ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }
}
