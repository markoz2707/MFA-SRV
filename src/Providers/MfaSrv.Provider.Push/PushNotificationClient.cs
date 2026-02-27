using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Push;

public class PushNotificationClient
{
    private readonly HttpClient _httpClient;
    private readonly PushSettings _settings;
    private readonly ILogger<PushNotificationClient> _logger;

    public PushNotificationClient(HttpClient httpClient, IOptions<PushSettings> settings, ILogger<PushNotificationClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendPushAsync(string deviceToken, string title, string body, string challengeId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.FcmServerKey))
        {
            _logger.LogWarning("FCM server key not configured. Push challenge {ChallengeId} logged only", challengeId);
            _logger.LogInformation("PUSH to device {Device}: {Title} - {Body} [ChallengeId: {ChallengeId}]",
                deviceToken, title, body, challengeId);
            return true;
        }

        try
        {
            var payload = new
            {
                to = deviceToken,
                notification = new { title, body },
                data = new { challengeId, action = "mfa_approve" }
            };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"key={_settings.FcmServerKey}");

            var response = await _httpClient.PostAsJsonAsync(_settings.FcmSendUrl, payload, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Push notification sent for challenge {ChallengeId}", challengeId);
                return true;
            }

            _logger.LogError("FCM returned {StatusCode} for challenge {ChallengeId}", response.StatusCode, challengeId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send push for challenge {ChallengeId}", challengeId);
            return false;
        }
    }
}
