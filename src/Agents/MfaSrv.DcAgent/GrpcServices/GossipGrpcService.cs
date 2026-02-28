using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.DcAgent.Services;
using MfaSrv.Protocol.Gossip;

namespace MfaSrv.DcAgent.GrpcServices;

/// <summary>
/// Server-side gRPC handler for the gossip protocol.
/// Receives session sync requests and broadcasts from peer DC Agents.
/// </summary>
public class GossipGrpcService : Protocol.Gossip.GossipService.GossipServiceBase
{
    private readonly SessionCacheService _sessionCache;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<GossipGrpcService> _logger;

    public GossipGrpcService(
        SessionCacheService sessionCache,
        IOptions<DcAgentSettings> settings,
        ILogger<GossipGrpcService> logger)
    {
        _sessionCache = sessionCache;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Handles a full session sync exchange. Merges incoming sessions from the
    /// requesting peer and returns our local sessions.
    /// </summary>
    public override Task<SyncSessionsResponse> SyncSessions(
        SyncSessionsRequest request, ServerCallContext context)
    {
        _logger.LogDebug(
            "SyncSessions from peer {PeerId} with {Count} sessions",
            request.SenderAgentId, request.Sessions.Count);

        // Merge incoming sessions into our local cache
        foreach (var entry in request.Sessions)
        {
            MergeSessionEntry(entry);
        }

        // Build our response with local sessions
        var response = new SyncSessionsResponse
        {
            ServerTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        foreach (var session in _sessionCache.GetAllSessions())
        {
            response.Sessions.Add(new SessionEntry
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

        _logger.LogDebug(
            "SyncSessions responding with {Count} sessions to peer {PeerId}",
            response.Sessions.Count, request.SenderAgentId);

        return Task.FromResult(response);
    }

    /// <summary>
    /// Handles a single session broadcast from a peer.
    /// </summary>
    public override Task<BroadcastSessionResponse> BroadcastSession(
        BroadcastSessionRequest request, ServerCallContext context)
    {
        _logger.LogDebug(
            "BroadcastSession from peer {PeerId}: session {SessionId}",
            request.SenderAgentId, request.Session?.SessionId);

        if (request.Session != null)
        {
            MergeSessionEntry(request.Session);
        }

        return Task.FromResult(new BroadcastSessionResponse
        {
            Acknowledged = true
        });
    }

    /// <summary>
    /// Responds to health/discovery pings from peers.
    /// </summary>
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        _logger.LogDebug("Ping from peer {PeerId} ({Hostname})",
            request.SenderAgentId, request.Hostname);

        return Task.FromResult(new PingResponse
        {
            AgentId = _settings.AgentId,
            Hostname = Environment.MachineName,
            ActiveSessions = _sessionCache.ActiveSessionCount,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        });
    }

    private void MergeSessionEntry(SessionEntry entry)
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
}
