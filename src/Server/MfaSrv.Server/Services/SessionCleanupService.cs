using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Interfaces;

namespace MfaSrv.Server.Services;

public class SessionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly SetupService _setupService;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public SessionCleanupService(IServiceScopeFactory scopeFactory, ILogger<SessionCleanupService> logger, SetupService setupService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _setupService = setupService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_setupService.IsSetupRequired())
        {
            _logger.LogInformation("Session cleanup service paused - awaiting initial setup");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
                await sessionManager.CleanupExpiredSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up expired sessions");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
