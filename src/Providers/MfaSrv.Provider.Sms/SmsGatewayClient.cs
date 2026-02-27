using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Sms;

public class SmsGatewayClient
{
    private readonly HttpClient _httpClient;
    private readonly SmsSettings _settings;
    private readonly ILogger<SmsGatewayClient> _logger;

    public SmsGatewayClient(HttpClient httpClient, IOptions<SmsSettings> settings, ILogger<SmsGatewayClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendSmsAsync(string toNumber, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.GatewayUrl))
        {
            _logger.LogWarning("SMS gateway URL not configured, logging OTP to console");
            _logger.LogInformation("SMS OTP to {Number}: {Message}", toNumber, message);
            return true; // Dev mode - log instead of send
        }

        try
        {
            var payload = new { to = toNumber, from = _settings.FromNumber, body = message };
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);

            var response = await _httpClient.PostAsJsonAsync(_settings.GatewayUrl, payload, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("SMS sent successfully to {Number}", toNumber);
                return true;
            }

            _logger.LogError("SMS gateway returned {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {Number}", toNumber);
            return false;
        }
    }
}
