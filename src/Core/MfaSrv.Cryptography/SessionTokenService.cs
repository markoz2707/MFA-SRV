using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MfaSrv.Core.Interfaces;

namespace MfaSrv.Cryptography;

/// <summary>
/// Compact binary session token with HMAC-SHA256 signature (~120 bytes).
/// Format: [Version:1][SessionIdLen:1][SessionId:N][UserIdLen:1][UserId:N][Expiry:8][HMAC:32]
/// </summary>
public class SessionTokenService : ITokenService
{
    private const byte TokenVersion = 1;
    private readonly byte[] _signingKey;

    public SessionTokenService(byte[] signingKey)
    {
        if (signingKey.Length < 32)
            throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(signingKey));
        _signingKey = signingKey;
    }

    public byte[] GenerateSessionToken(string sessionId, string userId, DateTimeOffset expiry)
    {
        var sessionIdBytes = Encoding.UTF8.GetBytes(sessionId);
        var userIdBytes = Encoding.UTF8.GetBytes(userId);

        if (sessionIdBytes.Length > 255 || userIdBytes.Length > 255)
            throw new ArgumentException("Session ID and User ID must be <= 255 bytes when UTF-8 encoded.");

        // Payload: Version(1) + SessionIdLen(1) + SessionId(N) + UserIdLen(1) + UserId(N) + Expiry(8)
        var payloadLength = 1 + 1 + sessionIdBytes.Length + 1 + userIdBytes.Length + 8;
        var token = new byte[payloadLength + 32]; // +32 for HMAC

        var offset = 0;
        token[offset++] = TokenVersion;

        token[offset++] = (byte)sessionIdBytes.Length;
        sessionIdBytes.CopyTo(token, offset);
        offset += sessionIdBytes.Length;

        token[offset++] = (byte)userIdBytes.Length;
        userIdBytes.CopyTo(token, offset);
        offset += userIdBytes.Length;

        BinaryPrimitives.WriteInt64BigEndian(token.AsSpan(offset), expiry.ToUnixTimeMilliseconds());
        offset += 8;

        // HMAC over the payload
        using var hmac = new HMACSHA256(_signingKey);
        var hash = hmac.ComputeHash(token, 0, payloadLength);
        hash.CopyTo(token, offset);

        return token;
    }

    public SessionTokenPayload? ValidateSessionToken(byte[] token)
    {
        try
        {
            if (token.Length < 1 + 1 + 1 + 1 + 1 + 8 + 32) // minimum size
                return null;

            var offset = 0;
            var version = token[offset++];
            if (version != TokenVersion)
                return null;

            var sessionIdLen = token[offset++];
            if (offset + sessionIdLen > token.Length - 32 - 1 - 8)
                return null;
            var sessionId = Encoding.UTF8.GetString(token, offset, sessionIdLen);
            offset += sessionIdLen;

            var userIdLen = token[offset++];
            if (offset + userIdLen > token.Length - 32 - 8)
                return null;
            var userId = Encoding.UTF8.GetString(token, offset, userIdLen);
            offset += userIdLen;

            var expiryMs = BinaryPrimitives.ReadInt64BigEndian(token.AsSpan(offset));
            offset += 8;

            var payloadLength = offset;

            // Verify HMAC
            using var hmac = new HMACSHA256(_signingKey);
            var expectedHash = hmac.ComputeHash(token, 0, payloadLength);
            var actualHash = token.AsSpan(payloadLength, 32);

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                return null;

            var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
            if (expiry < DateTimeOffset.UtcNow)
                return null;

            return new SessionTokenPayload(sessionId, userId, expiry);
        }
        catch
        {
            return null;
        }
    }
}
