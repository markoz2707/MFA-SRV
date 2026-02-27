using MfaSrv.Cryptography;

namespace MfaSrv.Provider.Totp;

/// <summary>
/// Standalone TOTP verification service that can decrypt and validate TOTP codes.
/// Used by the MfaChallengeOrchestrator when the provider's VerifyAsync needs decryption support.
/// </summary>
public class TotpVerificationService
{
    private readonly byte[] _encryptionKey;

    public TotpVerificationService(byte[] encryptionKey)
    {
        _encryptionKey = encryptionKey;
    }

    public bool Verify(byte[] encryptedSecret, byte[] nonce, string code)
    {
        try
        {
            var secret = AesGcmEncryption.Decrypt(encryptedSecret, nonce, _encryptionKey);
            return TotpGenerator.Validate(secret, code, DateTimeOffset.UtcNow);
        }
        catch
        {
            return false;
        }
    }
}
