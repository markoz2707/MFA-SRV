using Prometheus;

namespace MfaSrv.Server.Services;

/// <summary>
/// Centralized Prometheus metrics for MFA-SRV.
/// All counters, histograms, and gauges are defined here as static fields
/// for efficient thread-safe access from any service.
/// </summary>
public static class MetricsService
{
    // ── Authentication Metrics ──────────────────────────────────────────

    public static readonly Counter AuthEvaluationsTotal = Metrics.CreateCounter(
        "mfasrv_auth_evaluations_total",
        "Total number of authentication evaluations",
        new CounterConfiguration
        {
            LabelNames = new[] { "decision" } // allow, deny, require_mfa, pending
        });

    public static readonly Histogram AuthEvaluationDuration = Metrics.CreateHistogram(
        "mfasrv_auth_evaluation_duration_seconds",
        "Duration of authentication evaluations",
        new HistogramConfiguration
        {
            Buckets = Histogram.LinearBuckets(0.001, 0.005, 20) // 1ms to 100ms
        });

    // ── MFA Challenge Metrics ───────────────────────────────────────────

    public static readonly Counter MfaChallengesIssuedTotal = Metrics.CreateCounter(
        "mfasrv_mfa_challenges_issued_total",
        "Total MFA challenges issued",
        new CounterConfiguration
        {
            LabelNames = new[] { "method" } // TOTP, PUSH, FIDO2, FORTITOKEN, SMS, EMAIL
        });

    public static readonly Counter MfaVerificationsTotal = Metrics.CreateCounter(
        "mfasrv_mfa_verifications_total",
        "Total MFA verification attempts",
        new CounterConfiguration
        {
            LabelNames = new[] { "method", "result" } // method + success/failure
        });

    public static readonly Histogram MfaVerificationDuration = Metrics.CreateHistogram(
        "mfasrv_mfa_verification_duration_seconds",
        "Duration of MFA verification requests",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method" },
            Buckets = Histogram.LinearBuckets(0.01, 0.05, 20) // 10ms to 1s
        });

    // ── Session Metrics ─────────────────────────────────────────────────

    public static readonly Gauge ActiveSessionsCount = Metrics.CreateGauge(
        "mfasrv_active_sessions",
        "Number of currently active MFA sessions");

    public static readonly Counter SessionsCreatedTotal = Metrics.CreateCounter(
        "mfasrv_sessions_created_total",
        "Total sessions created");

    public static readonly Counter SessionsRevokedTotal = Metrics.CreateCounter(
        "mfasrv_sessions_revoked_total",
        "Total sessions revoked");

    public static readonly Counter SessionsExpiredTotal = Metrics.CreateCounter(
        "mfasrv_sessions_expired_total",
        "Total sessions expired during cleanup");

    // ── Agent Metrics ───────────────────────────────────────────────────

    public static readonly Gauge RegisteredAgentsCount = Metrics.CreateGauge(
        "mfasrv_registered_agents",
        "Number of registered agents",
        new GaugeConfiguration
        {
            LabelNames = new[] { "type" } // dc, endpoint
        });

    public static readonly Counter AgentHeartbeatsTotal = Metrics.CreateCounter(
        "mfasrv_agent_heartbeats_total",
        "Total agent heartbeats received",
        new CounterConfiguration
        {
            LabelNames = new[] { "agent_id" }
        });

    // ── Policy Metrics ──────────────────────────────────────────────────

    public static readonly Gauge ActivePoliciesCount = Metrics.CreateGauge(
        "mfasrv_active_policies",
        "Number of active policies");

    public static readonly Counter PolicyEvaluationsTotal = Metrics.CreateCounter(
        "mfasrv_policy_evaluations_total",
        "Total policy evaluations",
        new CounterConfiguration
        {
            LabelNames = new[] { "action" } // require_mfa, deny, allow, alert_only
        });

    // ── gRPC Metrics ────────────────────────────────────────────────────

    public static readonly Counter GrpcCallsTotal = Metrics.CreateCounter(
        "mfasrv_grpc_calls_total",
        "Total gRPC calls received",
        new CounterConfiguration
        {
            LabelNames = new[] { "method", "status" }
        });

    public static readonly Histogram GrpcCallDuration = Metrics.CreateHistogram(
        "mfasrv_grpc_call_duration_seconds",
        "Duration of gRPC calls",
        new HistogramConfiguration
        {
            LabelNames = new[] { "method" },
            Buckets = Histogram.LinearBuckets(0.001, 0.01, 20)
        });

    // ── Database Metrics ────────────────────────────────────────────────

    public static readonly Counter DbBackupsTotal = Metrics.CreateCounter(
        "mfasrv_db_backups_total",
        "Total database backups completed",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" } // success, failure
        });

    public static readonly Gauge DbSizeBytes = Metrics.CreateGauge(
        "mfasrv_db_size_bytes",
        "Current database file size in bytes");

    // ── HA Metrics ──────────────────────────────────────────────────────

    public static readonly Gauge IsLeader = Metrics.CreateGauge(
        "mfasrv_is_leader",
        "Whether this instance is the active leader (1=leader, 0=standby)");

    public static readonly Counter LeaderElectionsTotal = Metrics.CreateCounter(
        "mfasrv_leader_elections_total",
        "Total leader election events");

    // ── Enrollment Metrics ──────────────────────────────────────────────

    public static readonly Counter EnrollmentsTotal = Metrics.CreateCounter(
        "mfasrv_enrollments_total",
        "Total MFA enrollments",
        new CounterConfiguration
        {
            LabelNames = new[] { "method", "result" } // method + success/failure
        });

    public static readonly Gauge TotalEnrolledUsers = Metrics.CreateGauge(
        "mfasrv_enrolled_users",
        "Total users with at least one MFA enrollment");
}
