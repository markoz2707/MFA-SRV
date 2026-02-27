using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Email;

public class EmailSender
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<EmailSettings> settings, ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (_settings.SmtpHost == "localhost" && _settings.SmtpPort == 25
            && string.IsNullOrEmpty(_settings.SmtpUsername))
        {
            _logger.LogWarning("SMTP not configured, logging email to console");
            _logger.LogInformation("Email to {Address} | Subject: {Subject} | Body: {Body}", toAddress, subject, htmlBody);
            return true; // Dev mode - log instead of send
        }

        try
        {
            using var smtpClient = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort);
            smtpClient.EnableSsl = _settings.UseSsl;

            if (!string.IsNullOrEmpty(_settings.SmtpUsername) && !string.IsNullOrEmpty(_settings.SmtpPassword))
            {
                smtpClient.Credentials = new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword);
            }

            var fromAddress = new MailAddress(_settings.FromAddress, _settings.FromName);
            var toMailAddress = new MailAddress(toAddress);

            using var message = new MailMessage(fromAddress, toMailAddress)
            {
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };

            await smtpClient.SendMailAsync(message, ct);

            _logger.LogInformation("Email sent successfully to {Address}", toAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Address}", toAddress);
            return false;
        }
    }
}
