using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.FortiToken;

/// <summary>
/// HTTP client for FortiAuthenticator REST API.
/// Handles token-based OTP authentication, push authentication, and token management.
/// </summary>
public class FortiAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly FortiTokenSettings _settings;
    private readonly ILogger<FortiAuthClient> _logger;

    public FortiAuthClient(
        HttpClient httpClient,
        IOptions<FortiTokenSettings> settings,
        ILogger<FortiAuthClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    // ── OTP Authentication ───────────────────────────────────────────────

    /// <summary>
    /// Validates a username + OTP token code against FortiAuthenticator.
    /// POST /api/v1/auth/
    /// </summary>
    public async Task<FortiAuthResult> AuthenticateAsync(
        string username, string tokenCode, CancellationToken ct = default)
    {
        if (IsDevMode())
        {
            _logger.LogWarning(
                "FortiAuthenticator URL not configured (dev mode). " +
                "OTP auth for user {Username} with code {Code} auto-approved",
                username, MaskCode(tokenCode));
            return FortiAuthResult.Ok();
        }

        try
        {
            var payload = new { username, token_code = tokenCode };
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/auth/", payload);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "FortiAuth OTP authentication succeeded for user {Username}", username);
                return FortiAuthResult.Ok();
            }

            var body = await ReadErrorBody(response, ct);
            _logger.LogWarning(
                "FortiAuth OTP authentication failed for user {Username}: {StatusCode} - {Body}",
                username, response.StatusCode, body);

            return FortiAuthResult.Fail($"Authentication failed ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FortiAuth OTP authentication error for user {Username}", username);
            return FortiAuthResult.Fail($"FortiAuthenticator communication error: {ex.Message}");
        }
    }

    // ── Push Authentication ──────────────────────────────────────────────

    /// <summary>
    /// Triggers a FortiToken Mobile push notification for the given user.
    /// POST /api/v1/pushauth/
    /// Returns the session ID used to poll for the push result.
    /// </summary>
    public async Task<FortiAuthPushResult> PushAuthenticateAsync(
        string username, CancellationToken ct = default)
    {
        if (IsDevMode())
        {
            var devSessionId = Guid.NewGuid().ToString("N");
            _logger.LogWarning(
                "FortiAuthenticator URL not configured (dev mode). " +
                "Push auth for user {Username} auto-issued with session {SessionId}",
                username, devSessionId);
            return FortiAuthPushResult.Issued(devSessionId);
        }

        try
        {
            var payload = new { username };
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/pushauth/", payload);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<PushAuthResponse>(cancellationToken: ct);

                var sessionId = result?.SessionId ?? result?.Id;
                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger.LogError("FortiAuth push response missing session ID for user {Username}", username);
                    return FortiAuthPushResult.Fail("Push response did not contain a session ID");
                }

                _logger.LogInformation(
                    "FortiAuth push issued for user {Username}, session {SessionId}",
                    username, sessionId);

                return FortiAuthPushResult.Issued(sessionId);
            }

            var body = await ReadErrorBody(response, ct);
            _logger.LogWarning(
                "FortiAuth push failed for user {Username}: {StatusCode} - {Body}",
                username, response.StatusCode, body);

            return FortiAuthPushResult.Fail($"Push notification failed ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FortiAuth push error for user {Username}", username);
            return FortiAuthPushResult.Fail($"FortiAuthenticator communication error: {ex.Message}");
        }
    }

    /// <summary>
    /// Polls the status of a previously issued push authentication request.
    /// GET /api/v1/pushauth/{sessionId}/
    /// </summary>
    public async Task<FortiAuthPushStatusResult> CheckPushStatusAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (IsDevMode())
        {
            _logger.LogWarning(
                "FortiAuthenticator URL not configured (dev mode). " +
                "Push status for session {SessionId} returns 'approved'",
                sessionId);
            return FortiAuthPushStatusResult.WithStatus("approved");
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/api/v1/pushauth/{sessionId}/");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<PushStatusResponse>(cancellationToken: ct);

                var status = result?.Status?.ToLowerInvariant() ?? "pending";

                _logger.LogDebug(
                    "FortiAuth push status for session {SessionId}: {Status}",
                    sessionId, status);

                return FortiAuthPushStatusResult.WithStatus(status);
            }

            var body = await ReadErrorBody(response, ct);
            _logger.LogWarning(
                "FortiAuth push status check failed for session {SessionId}: {StatusCode} - {Body}",
                sessionId, response.StatusCode, body);

            return FortiAuthPushStatusResult.Failed(
                $"Push status check failed ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FortiAuth push status error for session {SessionId}", sessionId);
            return FortiAuthPushStatusResult.Failed(
                $"FortiAuthenticator communication error: {ex.Message}");
        }
    }

    // ── Token Management ─────────────────────────────────────────────────

    /// <summary>
    /// Lists tokens assigned to a user.
    /// GET /api/v1/usertokens/?username={username}
    /// </summary>
    public async Task<FortiAuthTokenListResult> GetUserTokensAsync(
        string username, CancellationToken ct = default)
    {
        if (IsDevMode())
        {
            _logger.LogWarning(
                "FortiAuthenticator URL not configured (dev mode). " +
                "Returning empty token list for user {Username}", username);
            return FortiAuthTokenListResult.Ok(Array.Empty<FortiTokenInfo>());
        }

        try
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"/api/v1/usertokens/?username={Uri.EscapeDataString(username)}");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<UserTokensResponse>(cancellationToken: ct);

                var tokens = result?.Results ?? Array.Empty<FortiTokenInfo>();

                _logger.LogInformation(
                    "Retrieved {Count} token(s) for user {Username}", tokens.Length, username);

                return FortiAuthTokenListResult.Ok(tokens);
            }

            var body = await ReadErrorBody(response, ct);
            _logger.LogWarning(
                "FortiAuth token list failed for user {Username}: {StatusCode} - {Body}",
                username, response.StatusCode, body);

            return FortiAuthTokenListResult.Fail(
                $"Failed to retrieve tokens ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FortiAuth token list error for user {Username}", username);
            return FortiAuthTokenListResult.Fail(
                $"FortiAuthenticator communication error: {ex.Message}");
        }
    }

    /// <summary>
    /// Assigns a hardware/software token to a user.
    /// POST /api/v1/usertokens/
    /// </summary>
    public async Task<FortiAuthResult> AssignTokenAsync(
        string username, string serialNumber, CancellationToken ct = default)
    {
        if (IsDevMode())
        {
            _logger.LogWarning(
                "FortiAuthenticator URL not configured (dev mode). " +
                "Token {SerialNumber} auto-assigned to user {Username}",
                serialNumber, username);
            return FortiAuthResult.Ok();
        }

        try
        {
            var payload = new { username, serial_number = serialNumber };
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/usertokens/", payload);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Token {SerialNumber} assigned to user {Username}", serialNumber, username);
                return FortiAuthResult.Ok();
            }

            var body = await ReadErrorBody(response, ct);
            _logger.LogWarning(
                "FortiAuth token assignment failed for user {Username}, serial {SerialNumber}: {StatusCode} - {Body}",
                username, serialNumber, response.StatusCode, body);

            return FortiAuthResult.Fail(
                $"Token assignment failed ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FortiAuth token assignment error for user {Username}, serial {SerialNumber}",
                username, serialNumber);
            return FortiAuthResult.Fail(
                $"FortiAuthenticator communication error: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool IsDevMode() => string.IsNullOrEmpty(_settings.FortiAuthUrl);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? jsonBody = null)
    {
        var url = _settings.FortiAuthUrl.TrimEnd('/') + path;
        var request = new HttpRequestMessage(method, url);

        // FortiAuthenticator supports API-key authentication via the Authorization header.
        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.ApiKey}");
        }
        else if (!string.IsNullOrEmpty(_settings.AdminUser))
        {
            // Fall back to Basic auth if no API key is provided.
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_settings.AdminUser}:{_settings.AdminPassword}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
        }

        if (jsonBody != null)
        {
            request.Content = JsonContent.Create(jsonBody);
        }

        return request;
    }

    private static async Task<string> ReadErrorBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? "(empty)" : body;
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static string MaskCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length <= 2)
            return "***";
        return code[..1] + new string('*', code.Length - 2) + code[^1..];
    }

    // ── Response models ─────────────────────────────────────────────────

    private sealed class PushAuthResponse
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class PushStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class UserTokensResponse
    {
        [JsonPropertyName("results")]
        public FortiTokenInfo[]? Results { get; set; }
    }
}

// ── Public result types ──────────────────────────────────────────────────

/// <summary>
/// Generic success/failure result from FortiAuthenticator API calls.
/// </summary>
public sealed class FortiAuthResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }

    public static FortiAuthResult Ok() => new() { Success = true };
    public static FortiAuthResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Result of a push authentication request, including the session ID for polling.
/// </summary>
public sealed class FortiAuthPushResult
{
    public bool Success { get; private init; }
    public string? SessionId { get; private init; }
    public string? Error { get; private init; }

    public static FortiAuthPushResult Issued(string sessionId) =>
        new() { Success = true, SessionId = sessionId };

    public static FortiAuthPushResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Result of polling a push authentication session.
/// Status values: "approved", "denied", "pending", or an error string.
/// </summary>
public sealed class FortiAuthPushStatusResult
{
    public bool Success { get; private init; }
    public string Status { get; private init; } = "pending";
    public string? Error { get; private init; }

    public static FortiAuthPushStatusResult WithStatus(string status) =>
        new() { Success = true, Status = status };

    public static FortiAuthPushStatusResult Failed(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Result of listing a user's assigned tokens.
/// </summary>
public sealed class FortiAuthTokenListResult
{
    public bool Success { get; private init; }
    public FortiTokenInfo[] Tokens { get; private init; } = Array.Empty<FortiTokenInfo>();
    public string? Error { get; private init; }

    public static FortiAuthTokenListResult Ok(FortiTokenInfo[] tokens) =>
        new() { Success = true, Tokens = tokens };

    public static FortiAuthTokenListResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Represents a FortiToken assigned to a user.
/// </summary>
public sealed class FortiTokenInfo
{
    [JsonPropertyName("serial_number")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
