using MfaSrv.DcAgent;
using MfaSrv.DcAgent.GrpcServices;
using MfaSrv.DcAgent.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MfaSrv DC Agent";
});

// gRPC server for gossip protocol
builder.Services.AddGrpc();

// Configuration
builder.Services.Configure<DcAgentSettings>(builder.Configuration.GetSection("DcAgent"));

// SQLite persistent cache store (singleton, shared by policy and session caches)
builder.Services.AddSingleton<SqliteCacheStore>(sp =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DcAgentSettings>>().Value;
    var logger = sp.GetRequiredService<ILogger<SqliteCacheStore>>();
    return new SqliteCacheStore(settings.CacheDbPath, logger);
});

// Core services
builder.Services.AddSingleton<PolicyCacheService>();
builder.Services.AddSingleton<SessionCacheService>();
builder.Services.AddSingleton<AuthDecisionService>();
builder.Services.AddSingleton<FailoverManager>();

// Background services
builder.Services.AddHostedService<NamedPipeServer>();
builder.Services.AddHostedService<CentralServerClient>();
builder.Services.AddHostedService<GossipService>();
builder.Services.AddHostedService<PolicySyncClient>();

var app = builder.Build();

// Initialize SQLite cache store and hydrate in-memory caches before starting the host
var initLogger = app.Services.GetRequiredService<ILogger<Program>>();
try
{
    var cacheStore = app.Services.GetRequiredService<SqliteCacheStore>();
    await cacheStore.InitializeAsync();

    var policyCache = app.Services.GetRequiredService<PolicyCacheService>();
    await policyCache.InitializeAsync();

    var sessionCache = app.Services.GetRequiredService<SessionCacheService>();
    await sessionCache.InitializeAsync();

    initLogger.LogInformation("SQLite-backed cache initialization complete");
}
catch (Exception ex)
{
    initLogger.LogError(ex, "Failed to initialize SQLite cache; service will start with empty caches");
}

// Map gRPC gossip endpoint for peer DC Agents
app.MapGrpcService<GossipGrpcService>();

app.Run();
