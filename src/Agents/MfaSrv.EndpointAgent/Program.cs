using MfaSrv.EndpointAgent;
using MfaSrv.EndpointAgent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MfaSrv Endpoint Agent";
});

// Configuration
builder.Services.Configure<EndpointAgentSettings>(builder.Configuration.GetSection("EndpointAgent"));

// Core services
builder.Services.AddSingleton<EndpointFailoverManager>();
builder.Services.AddSingleton<EndpointSessionCache>();
builder.Services.AddSingleton<CentralServerClient>();
builder.Services.AddSingleton<YubiKeyService>();

// Background services
builder.Services.AddHostedService<NamedPipeServer>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<EndpointAgentWorker>();

var host = builder.Build();
host.Run();
