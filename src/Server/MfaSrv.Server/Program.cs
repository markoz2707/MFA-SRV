using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Interfaces;
using MfaSrv.Cryptography;
using MfaSrv.Server;
using MfaSrv.Server.Data;
using MfaSrv.Server.GrpcServices;
using MfaSrv.Server.Services;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<MfaSrvDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=mfasrv.db"));

// gRPC
builder.Services.AddGrpc();

// REST API
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();

// LDAP settings
builder.Services.Configure<LdapSettings>(builder.Configuration.GetSection("Ldap"));

// HA settings
builder.Services.Configure<HaSettings>(builder.Configuration.GetSection("HA"));

// Token signing key
var signingKeyBase64 = builder.Configuration["MfaSrv:TokenSigningKey"];
byte[] signingKey;
if (!string.IsNullOrEmpty(signingKeyBase64))
{
    signingKey = Convert.FromBase64String(signingKeyBase64);
}
else
{
    signingKey = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(signingKey);
}

// Core services
builder.Services.AddSingleton<ITokenService>(new SessionTokenService(signingKey));
builder.Services.AddSingleton<PolicySyncStreamService>();
builder.Services.AddScoped<IPolicyEngine, PolicyEngine>();
builder.Services.AddScoped<ISessionManager, SessionManager>();
builder.Services.AddScoped<IMfaChallengeOrchestrator, MfaChallengeOrchestrator>();
builder.Services.AddScoped<IAuditLogger, AuditLogService>();
builder.Services.AddScoped<IUserSyncService, UserSyncService>();

// MFA Providers - register all available providers
builder.Services.AddSingleton<IMfaProvider, MfaSrv.Provider.Totp.TotpMfaProvider>();

// Backup services
builder.Services.Configure<BackupSettings>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackupService>());
builder.Services.AddScoped<DatabaseExportService>();

// HA - Leader election
builder.Services.AddSingleton<LeaderElectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaderElectionService>());

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<MfaSrvHealthCheck>("mfasrv", tags: new[] { "live" })
    .AddCheck<MfaSrvReadinessCheck>("readiness", tags: new[] { "ready" })
    .AddDbContextCheck<MfaSrvDbContext>("database", tags: new[] { "live", "ready" });

// Background services
builder.Services.AddHostedService<SessionCleanupService>();

// CORS for admin portal
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminPortal", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:3000" })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Auto-migrate database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("AdminPortal");

// Prometheus metrics endpoint
app.UseHttpMetrics();
app.MapMetrics("/metrics");

app.MapControllers();
app.MapGrpcService<MfaGrpcService>();

// Health endpoints
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse
});

app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
});

// Detailed status endpoint
app.MapGet("/status", (LeaderElectionService leader) => Results.Ok(new
{
    status = "running",
    timestamp = DateTimeOffset.UtcNow,
    instanceId = leader.InstanceId,
    isLeader = leader.IsLeader,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"
}));

app.Run();

// Health check response writer
static async Task WriteHealthResponse(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";

    var result = new
    {
        status = report.Status.ToString(),
        timestamp = DateTimeOffset.UtcNow,
        duration = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            duration = e.Value.Duration.TotalMilliseconds,
            data = e.Value.Data,
            error = e.Value.Exception?.Message
        })
    };

    await context.Response.WriteAsJsonAsync(result);
}
