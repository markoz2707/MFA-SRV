using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Enums;
using MfaSrv.Server.Data;
using MfaSrv.Server.Services;

namespace MfaSrv.Server.Controllers;

/// <summary>
/// Provides aggregated statistics and recent activity for the admin dashboard.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly PolicySyncStreamService _policySyncStream;

    public DashboardController(MfaSrvDbContext db, PolicySyncStreamService policySyncStream)
    {
        _db = db;
        _policySyncStream = policySyncStream;
    }

    /// <summary>
    /// Returns a comprehensive snapshot of system statistics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var last24h = now.AddHours(-24);

        // User statistics
        var totalUsers = await _db.Users.CountAsync();
        var mfaEnabledUsers = await _db.Users.CountAsync(u => u.MfaEnabled);

        // Session statistics
        var activeSessions = await _db.MfaSessions
            .CountAsync(s => s.Status == SessionStatus.Active && s.ExpiresAt > now);

        // Agent statistics
        var onlineAgents = await _db.AgentRegistrations
            .CountAsync(a => a.Status == AgentStatus.Online);
        var totalAgents = await _db.AgentRegistrations.CountAsync();

        // Audit event counts for last 24 hours
        var authEvents24h = await _db.AuditLog
            .CountAsync(e => e.Timestamp >= last24h && e.EventType == AuditEventType.AuthenticationAttempt);
        var mfaChallenges24h = await _db.AuditLog
            .CountAsync(e => e.Timestamp >= last24h && e.EventType == AuditEventType.MfaChallengeIssued);
        var mfaSuccesses24h = await _db.AuditLog
            .CountAsync(e => e.Timestamp >= last24h && e.EventType == AuditEventType.MfaChallengeVerified);
        var mfaFailures24h = await _db.AuditLog
            .CountAsync(e => e.Timestamp >= last24h && e.EventType == AuditEventType.MfaChallengeFailed);
        var deniedAuths24h = await _db.AuditLog
            .CountAsync(e => e.Timestamp >= last24h
                && e.EventType == AuditEventType.AuthenticationAttempt
                && !e.Success);

        // Enrollment breakdown by method
        var enrollmentsByMethod = await _db.MfaEnrollments
            .Where(e => e.Status == EnrollmentStatus.Active)
            .GroupBy(e => e.Method)
            .Select(g => new { Method = g.Key.ToString(), Count = g.Count() })
            .ToListAsync();

        // Policy statistics
        var activePolicies = await _db.Policies.CountAsync(p => p.IsEnabled);
        var totalPolicies = await _db.Policies.CountAsync();

        // Recent audit events
        var recentEvents = await _db.AuditLog
            .OrderByDescending(e => e.Timestamp)
            .Take(10)
            .Select(e => new
            {
                e.EventType,
                e.UserId,
                e.UserName,
                e.SourceIp,
                e.Timestamp,
                e.Success,
                e.Details
            })
            .ToListAsync();

        return Ok(new
        {
            users = new { total = totalUsers, mfaEnabled = mfaEnabledUsers },
            sessions = new { active = activeSessions },
            agents = new
            {
                online = onlineAgents,
                total = totalAgents,
                syncSubscribers = _policySyncStream.SubscriberCount
            },
            policies = new { active = activePolicies, total = totalPolicies },
            last24Hours = new
            {
                authentications = authEvents24h,
                mfaChallenges = mfaChallenges24h,
                mfaSuccesses = mfaSuccesses24h,
                mfaFailures = mfaFailures24h,
                denied = deniedAuths24h
            },
            enrollmentsByMethod,
            recentEvents
        });
    }

    /// <summary>
    /// Returns audit event counts grouped by hour and event type for charting.
    /// </summary>
    [HttpGet("stats/hourly")]
    public async Task<IActionResult> GetHourlyStats([FromQuery] int hours = 24)
    {
        if (hours < 1) hours = 1;
        if (hours > 168) hours = 168; // Cap at 7 days

        var since = DateTimeOffset.UtcNow.AddHours(-hours);

        var events = await _db.AuditLog
            .Where(e => e.Timestamp >= since)
            .GroupBy(e => new { Hour = e.Timestamp.Hour, e.EventType })
            .Select(g => new
            {
                g.Key.Hour,
                EventType = g.Key.EventType.ToString(),
                Count = g.Count()
            })
            .OrderBy(x => x.Hour)
            .ToListAsync();

        return Ok(events);
    }
}
