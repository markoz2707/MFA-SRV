using System.Security.Cryptography;

namespace MfaSrv.Cryptography;

/// <summary>
/// AES-256-GCM encryption for MFA enrollment secrets.
/// </summary>
public static class AesGcmEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static byte[] GenerateKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static (byte[] Ciphertext, byte[] Nonce) Encrypt(byte[] plaintext, byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length + TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            ciphertext.AsSpan(0, plaintext.Length),
            ciphertext.AsSpan(plaintext.Length, TagSize));

        return (ciphertext, nonce);
    }

    public static byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));

        var plaintextLength = ciphertext.Length - TagSize;
        if (plaintextLength < 0)
            throw new ArgumentException("Ciphertext too short.", nameof(ciphertext));

        var plaintext = new byte[plaintextLength];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            nonce,
            ciphertext.AsSpan(0, plaintextLength),
            ciphertext.AsSpan(plaintextLength, TagSize),
            plaintext);

        return plaintext;
    }
}
