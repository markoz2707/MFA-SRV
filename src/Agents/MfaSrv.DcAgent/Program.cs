using MfaSrv.DcAgent;
using MfaSrv.DcAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MfaSrv DC Agent";
});

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

var host = builder.Build();

// Initialize SQLite cache store and hydrate in-memory caches before starting the host
var initLogger = host.Services.GetRequiredService<ILogger<Program>>();
try
{
    var cacheStore = host.Services.GetRequiredService<SqliteCacheStore>();
    await cacheStore.InitializeAsync();

    var policyCache = host.Services.GetRequiredService<PolicyCacheService>();
    await policyCache.InitializeAsync();

    var sessionCache = host.Services.GetRequiredService<SessionCacheService>();
    await sessionCache.InitializeAsync();

    initLogger.LogInformation("SQLite-backed cache initialization complete");
}
catch (Exception ex)
{
    initLogger.LogError(ex, "Failed to initialize SQLite cache; service will start with empty caches");
}

host.Run();
