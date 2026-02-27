using System.Security.Cryptography;

namespace MfaSrv.Cryptography;

/// <summary>
/// RFC 6238 TOTP implementation.
/// </summary>
public static class TotpGenerator
{
    private const int DefaultDigits = 6;
    private const int DefaultPeriodSeconds = 30;
    private const int DefaultToleranceSteps = 1;

    public static byte[] GenerateSecret(int length = 20)
    {
        var secret = new byte[length];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    public static string ComputeTotp(byte[] secret, DateTimeOffset timestamp, int digits = DefaultDigits, int periodSeconds = DefaultPeriodSeconds)
    {
        var timeStep = GetTimeStep(timestamp, periodSeconds);
        return ComputeHotp(secret, timeStep, digits);
    }

    public static bool Validate(byte[] secret, string code, DateTimeOffset timestamp,
        int digits = DefaultDigits, int periodSeconds = DefaultPeriodSeconds, int toleranceSteps = DefaultToleranceSteps)
    {
        if (string.IsNullOrEmpty(code) || code.Length != digits)
            return false;

        var currentStep = GetTimeStep(timestamp, periodSeconds);

        for (var i = -toleranceSteps; i <= toleranceSteps; i++)
        {
            var step = currentStep + i;
            var expected = ComputeHotp(secret, step, digits);
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected),
                System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    public static string GenerateProvisioningUri(byte[] secret, string userName, string issuer)
    {
        var base32Secret = Base32.Encode(secret);
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedUser = Uri.EscapeDataString(userName);
        return $"otpauth://totp/{encodedIssuer}:{encodedUser}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={DefaultDigits}&period={DefaultPeriodSeconds}";
    }

    private static long GetTimeStep(DateTimeOffset timestamp, int periodSeconds)
    {
        return timestamp.ToUnixTimeSeconds() / periodSeconds;
    }

    private static string ComputeHotp(byte[] secret, long counter, int digits)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var otp = binaryCode % (int)Math.Pow(10, digits);
        return otp.ToString().PadLeft(digits, '0');
    }
}
