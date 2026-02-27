namespace MfaSrv.Cryptography;

/// <summary>
/// RFC 4648 Base32 encoding/decoding for TOTP secret sharing.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        if (data.Length == 0)
            return string.Empty;

        var chars = new char[(data.Length * 8 + 4) / 5];
        int buffer = data[0];
        int bitsLeft = 8;
        int index = 0;
        int dataIndex = 1;

        while (bitsLeft > 0 || dataIndex < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (dataIndex < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[dataIndex++] & 0xFF;
                    bitsLeft += 8;
                }
                else
                {
                    int pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }

            int charIndex = (buffer >> (bitsLeft - 5)) & 0x1F;
            bitsLeft -= 5;
            chars[index++] = Alphabet[charIndex];
        }

        return new string(chars, 0, index);
    }

    public static byte[] Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return Array.Empty<byte>();

        encoded = encoded.TrimEnd('=').ToUpperInvariant();
        var output = new byte[encoded.Length * 5 / 8];
        int buffer = 0;
        int bitsLeft = 0;
        int outputIndex = 0;

        foreach (var c in encoded)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0)
                throw new FormatException($"Invalid Base32 character: {c}");

            buffer <<= 5;
            buffer |= val & 0x1F;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                output[outputIndex++] = (byte)(buffer >> (bitsLeft - 8));
                bitsLeft -= 8;
            }
        }

        return output[..outputIndex];
    }
}
