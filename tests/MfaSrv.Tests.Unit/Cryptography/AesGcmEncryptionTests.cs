using FluentAssertions;
using MfaSrv.Cryptography;
using Xunit;

namespace MfaSrv.Tests.Unit.Cryptography;

public class AesGcmEncryptionTests
{
    [Fact]
    public void GenerateKey_ReturnsCorrectLength()
    {
        var key = AesGcmEncryption.GenerateKey();
        key.Should().HaveCount(32);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesData()
    {
        var key = AesGcmEncryption.GenerateKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, MFA World!");

        var (ciphertext, nonce) = AesGcmEncryption.Encrypt(plaintext, key);
        var decrypted = AesGcmEncryption.Decrypt(ciphertext, nonce, key);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var key = AesGcmEncryption.GenerateKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("Same plaintext");

        var (ciphertext1, nonce1) = AesGcmEncryption.Encrypt(plaintext, key);
        var (ciphertext2, nonce2) = AesGcmEncryption.Encrypt(plaintext, key);

        // Different nonces should produce different ciphertext
        nonce1.Should().NotEqual(nonce2);
        ciphertext1.Should().NotEqual(ciphertext2);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var key1 = AesGcmEncryption.GenerateKey();
        var key2 = AesGcmEncryption.GenerateKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("Secret data");

        var (ciphertext, nonce) = AesGcmEncryption.Encrypt(plaintext, key1);

        var act = () => AesGcmEncryption.Decrypt(ciphertext, nonce, key2);
        act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var key = AesGcmEncryption.GenerateKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("Secret data");

        var (ciphertext, nonce) = AesGcmEncryption.Encrypt(plaintext, key);
        ciphertext[0] ^= 0xFF; // Tamper with first byte

        var act = () => AesGcmEncryption.Decrypt(ciphertext, nonce, key);
        act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
    }

    [Fact]
    public void Encrypt_InvalidKeyLength_ThrowsArgumentException()
    {
        var shortKey = new byte[16];
        var plaintext = new byte[] { 1, 2, 3 };

        var act = () => AesGcmEncryption.Encrypt(plaintext, shortKey);
        act.Should().Throw<ArgumentException>();
    }
}
