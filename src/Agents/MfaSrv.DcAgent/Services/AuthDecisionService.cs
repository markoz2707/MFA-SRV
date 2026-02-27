using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;

namespace MfaSrv.DcAgent.Services;

/// <summary>
/// Three-tier authentication decision engine:
/// 1. Local session cache (fastest) → ALLOW if valid cached MFA session exists
/// 2. Central Server gRPC call → authoritative decision
/// 3. Local policy cache with failover mode → degraded-mode decision
///
/// Failover modes (per-policy, with global default):
/// - FAIL_OPEN:   allow auth, log for audit
/// - FAIL_CLOSE:  deny auth
/// - CACHED_ONLY: allow only if a cached MFA session exists
/// </summary>
public class AuthDecisionService
{
    private readonly SessionCacheService _sessionCache;
    private readonly PolicyCacheService _policyCache;
    private readonly FailoverManager _failoverManager;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<AuthDecisionService> _logger;

    public AuthDecisionService(
        SessionCacheService sessionCache,
        PolicyCacheService policyCache,
        FailoverManager failoverManager,
        IOptions<DcAgentSettings> settings,
        ILogger<AuthDecisionService> logger)
    {
        _sessionCache = sessionCache;
        _policyCache = policyCache;
        _failoverManager = failoverManager;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<AuthResponseMessage> EvaluateAsync(AuthQueryMessage query, CancellationToken ct = default)
    {
        try
        {
            // 1. Check session cache first (fastest path)
            var cachedSession = _sessionCache.FindSession(query.UserName, query.SourceIp);
            if (cachedSession != null)
            {
                _logger.LogDebug(
                    "Found cached MFA session {SessionId} for {User} (method={Method}, expires={Expires})",
                    cachedSession.SessionId, query.UserName, cachedSession.VerifiedMethod, cachedSession.ExpiresAt);

                return new AuthResponseMessage
                {
                    Decision = AuthDecision.Allow,
                    SessionToken = cachedSession.SessionId,
                    Reason = "Cached MFA session valid"
                };
            }

            // 2. Try Central Server if available
            if (_failoverManager.IsCentralServerAvailable)
            {
                var serverResponse = await _failoverManager.EvaluateViaCentralServerAsync(query, ct);
                if (serverResponse != null)
                {
                    // Cache session tokens from successful Allow decisions
                    if (serverResponse.Decision == AuthDecision.Allow &&
                        !string.IsNullOrEmpty(serverResponse.SessionToken))
                    {
                        _sessionCache.AddOrUpdateSession(new CachedSession
                        {
                            SessionId = serverResponse.SessionToken,
                            UserId = query.UserName,
                            UserName = query.UserName,
                            SourceIp = query.SourceIp ?? string.Empty,
                            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.SessionTtlMinutes),
                            VerifiedMethod = "server",
                            Revoked = false
                        });
                    }

                    return serverResponse;
                }
            }

            // 3. Fallback to local policy evaluation with failover modes
            _logger.LogWarning(
                "Central server unavailable for auth eval: {User}@{Domain} from {Ip} via {Protocol}",
                query.UserName, query.Domain, query.SourceIp, query.Protocol);

            return EvaluateWithFailoverMode(query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating auth for {User}@{Domain}", query.UserName, query.Domain);

            // Fail-open on any unhandled exception
            return new AuthResponseMessage
            {
                Decision = AuthDecision.Allow,
                Reason = "Error during evaluation - fail-open"
            };
        }
    }

    /// <summary>
    /// Evaluates authentication using local policy cache and failover modes
    /// when the Central Server is unreachable.
    /// </summary>
    private AuthResponseMessage EvaluateWithFailoverMode(AuthQueryMessage query)
    {
        // Determine effective failover mode. Check matching policies first,
        // then fall back to the global agent setting.
        var failoverMode = ResolveFailoverMode(query);

        _logger.LogInformation(
            "Failover decision for {User}@{Domain}: mode={FailoverMode}, server_down_since={DownSince}",
            query.UserName, query.Domain, failoverMode,
            _failoverManager.LastSuccessfulContact);

        AuthResponseMessage response;

        switch (failoverMode)
        {
            case FailoverMode.FailOpen:
                response = new AuthResponseMessage
                {
                    Decision = AuthDecision.Allow,
                    Reason = $"Fail-open: central server unavailable (last contact: {FormatTimestamp(_failoverManager.LastSuccessfulContact)})"
                };
                _logger.LogWarning(
                    "FAIL_OPEN: Allowing {User}@{Domain} from {Ip} without MFA (server unavailable)",
                    query.UserName, query.Domain, query.SourceIp);
                break;

            case FailoverMode.FailClose:
                response = new AuthResponseMessage
                {
                    Decision = AuthDecision.Deny,
                    Reason = "Fail-close: central server unavailable, MFA verification not possible"
                };
                _logger.LogWarning(
                    "FAIL_CLOSE: Denying {User}@{Domain} from {Ip} (server unavailable, MFA required by policy)",
                    query.UserName, query.Domain, query.SourceIp);
                break;

            case FailoverMode.CachedOnly:
                var cachedSession = _sessionCache.FindSession(query.UserName, query.SourceIp);
                if (cachedSession != null)
                {
                    response = new AuthResponseMessage
                    {
                        Decision = AuthDecision.Allow,
                        SessionToken = cachedSession.SessionId,
                        Reason = $"Cached session valid (cached-only mode, method={cachedSession.VerifiedMethod})"
                    };
                    _logger.LogInformation(
                        "CACHED_ONLY: Allowing {User}@{Domain} via cached session {SessionId} (expires={Expires})",
                        query.UserName, query.Domain, cachedSession.SessionId, cachedSession.ExpiresAt);
                }
                else
                {
                    response = new AuthResponseMessage
                    {
                        Decision = AuthDecision.Deny,
                        Reason = "No cached session (cached-only mode, server unavailable)"
                    };
                    _logger.LogWarning(
                        "CACHED_ONLY: Denying {User}@{Domain} from {Ip} (no cached session, server unavailable)",
                        query.UserName, query.Domain, query.SourceIp);
                }
                break;

            default:
                response = new AuthResponseMessage
                {
                    Decision = AuthDecision.Allow,
                    Reason = "Default fail-open"
                };
                break;
        }

        return response;
    }

    /// <summary>
    /// Resolves the effective failover mode by checking matching cached policies
    /// (highest priority first). Falls back to the agent-level global setting.
    /// </summary>
    private FailoverMode ResolveFailoverMode(AuthQueryMessage query)
    {
        var policies = _policyCache.GetPolicies();

        // Find the highest-priority matching policy that specifies a failover mode
        foreach (var policy in policies)
        {
            if (PolicyMatchesQuery(policy, query))
            {
                return policy.FailoverMode;
            }
        }

        // Fall back to global setting from agent config
        if (Enum.TryParse<FailoverMode>(_settings.FailoverMode, ignoreCase: true, out var globalMode))
        {
            return globalMode;
        }

        return FailoverMode.FailOpen;
    }

    /// <summary>
    /// Lightweight check whether a cached policy applies to this auth query.
    /// Parses the policy JSON to check rule groups against the query context.
    /// </summary>
    private bool PolicyMatchesQuery(CachedPolicy policy, AuthQueryMessage query)
    {
        try
        {
            using var doc = JsonDocument.Parse(policy.PolicyJson);
            var root = doc.RootElement;

            // Check if the policy has rule groups
            if (!root.TryGetProperty("ruleGroups", out var ruleGroups) &&
                !root.TryGetProperty("RuleGroups", out ruleGroups))
            {
                // Policy with no rules matches everything
                return true;
            }

            // Rule groups use OR logic (any group match → policy matches)
            foreach (var group in ruleGroups.EnumerateArray())
            {
                if (RuleGroupMatchesQuery(group, query))
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // If we can't parse the policy, treat it as matching (conservative)
            return true;
        }
    }

    /// <summary>
    /// Checks if all rules in a rule group match the query (AND logic within group).
    /// </summary>
    private static bool RuleGroupMatchesQuery(JsonElement group, AuthQueryMessage query)
    {
        if (!group.TryGetProperty("rules", out var rules) &&
            !group.TryGetProperty("Rules", out rules))
        {
            return true;
        }

        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatchesQuery(rule, query))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a single rule matches the query.
    /// </summary>
    private static bool RuleMatchesQuery(JsonElement rule, AuthQueryMessage query)
    {
        var ruleTypeStr = rule.TryGetProperty("ruleType", out var rt)
            ? rt.GetString()
            : rule.TryGetProperty("RuleType", out rt) ? rt.GetString() : null;

        var value = rule.TryGetProperty("value", out var v)
            ? v.GetString()
            : rule.TryGetProperty("Value", out v) ? v.GetString() : null;

        var negate = rule.TryGetProperty("negate", out var n)
            ? n.GetBoolean()
            : rule.TryGetProperty("Negate", out n) && n.GetBoolean();

        if (ruleTypeStr == null || value == null)
            return true;

        bool matches = ruleTypeStr switch
        {
            "SOURCE_USER" or "SourceUser" =>
                string.Equals(query.UserName, value, StringComparison.OrdinalIgnoreCase),

            "SOURCE_IP" or "SourceIp" =>
                string.Equals(query.SourceIp, value, StringComparison.OrdinalIgnoreCase),

            "AUTH_PROTOCOL" or "AuthProtocol" =>
                string.Equals(query.Protocol.ToString(), value, StringComparison.OrdinalIgnoreCase),

            // For group/OU rules, we can't fully evaluate locally without AD access.
            // Conservative: treat as matching so the failover mode is applied.
            _ => true
        };

        return negate ? !matches : matches;
    }

    private static string FormatTimestamp(DateTimeOffset ts)
    {
        if (ts == DateTimeOffset.MinValue) return "never";
        var ago = DateTimeOffset.UtcNow - ts;
        if (ago.TotalSeconds < 60) return $"{ago.TotalSeconds:F0}s ago";
        if (ago.TotalMinutes < 60) return $"{ago.TotalMinutes:F0}m ago";
        return $"{ago.TotalHours:F1}h ago";
    }
}
