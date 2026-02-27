namespace MfaSrv.Core.Interfaces;

public interface ITokenService
{
    byte[] GenerateSessionToken(string sessionId, string userId, DateTimeOffset expiry);
    SessionTokenPayload? ValidateSessionToken(byte[] token);
}

public record SessionTokenPayload(string SessionId, string UserId, DateTimeOffset Expiry);
