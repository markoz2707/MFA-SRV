using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Protocol.Gossip;
using Google.Protobuf.WellKnownTypes;

namespace MfaSrv.DcAgent.Services;

public class GossipService : BackgroundService
{
    private readonly SessionCacheService _sessionCache;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<GossipService> _logger;

    public GossipService(
        SessionCacheService sessionCache,
        IOptions<DcAgentSettings> settings,
        ILogger<GossipService> logger)
    {
        _sessionCache = sessionCache;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settings.GossipPeers.Length == 0)
        {
            _logger.LogInformation("No gossip peers configured, gossip service disabled");
            return;
        }

        _logger.LogInformation("Gossip service starting with {PeerCount} peers", _settings.GossipPeers.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncWithPeersAsync(stoppingToken);
                _sessionCache.CleanupExpired();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in gossip sync cycle");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task SyncWithPeersAsync(CancellationToken ct)
    {
        foreach (var peerUrl in _settings.GossipPeers)
        {
            try
            {
                using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(peerUrl);
                var client = new Protocol.Gossip.GossipService.GossipServiceClient(channel);

                // Build our session list
                var request = new SyncSessionsRequest
                {
                    SenderAgentId = _settings.AgentId,
                    Since = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-10))
                };

                foreach (var session in _sessionCache.GetAllSessions())
                {
                    request.Sessions.Add(new SessionEntry
                    {
                        SessionId = session.SessionId,
                        UserId = session.UserId,
                        UserName = session.UserName,
                        SourceIp = session.SourceIp,
                        VerifiedMethod = session.VerifiedMethod,
                        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        ExpiresAt = Timestamp.FromDateTimeOffset(session.ExpiresAt),
                        Revoked = session.Revoked
                    });
                }

                var response = await client.SyncSessionsAsync(request, cancellationToken: ct);

                // Merge received sessions into our cache
                foreach (var entry in response.Sessions)
                {
                    if (entry.Revoked)
                    {
                        _sessionCache.RevokeSession(entry.SessionId);
                    }
                    else
                    {
                        var existing = _sessionCache.FindSession(entry.UserName, entry.SourceIp);
                        if (existing == null || existing.SessionId != entry.SessionId)
                        {
                            _sessionCache.AddOrUpdateSession(new CachedSession
                            {
                                SessionId = entry.SessionId,
                                UserId = entry.UserId,
                                UserName = entry.UserName,
                                SourceIp = entry.SourceIp,
                                VerifiedMethod = entry.VerifiedMethod,
                                ExpiresAt = entry.ExpiresAt.ToDateTimeOffset()
                            });
                        }
                    }
                }

                _logger.LogDebug("Synced {Count} sessions with peer {Peer}", response.Sessions.Count, peerUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync with peer {Peer}", peerUrl);
            }
        }
    }

    public async Task BroadcastNewSessionAsync(CachedSession session, CancellationToken ct = default)
    {
        foreach (var peerUrl in _settings.GossipPeers)
        {
            try
            {
                using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(peerUrl);
                var client = new Protocol.Gossip.GossipService.GossipServiceClient(channel);

                await client.BroadcastSessionAsync(new BroadcastSessionRequest
                {
                    SenderAgentId = _settings.AgentId,
                    Session = new SessionEntry
                    {
                        SessionId = session.SessionId,
                        UserId = session.UserId,
                        UserName = session.UserName,
                        SourceIp = session.SourceIp,
                        VerifiedMethod = session.VerifiedMethod,
                        ExpiresAt = Timestamp.FromDateTimeOffset(session.ExpiresAt)
                    }
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast session to peer {Peer}", peerUrl);
            }
        }
    }
}
