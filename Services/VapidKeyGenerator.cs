using System.Security.Cryptography;

namespace Splitbill.Services;

public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// Creates the URL-safe P-256 key material required by browser Web Push.
/// PublicKey is the uncompressed 65-byte EC point and PrivateKey is the
/// 32-byte private scalar, both encoded as base64url without padding.
/// </summary>
public static class VapidKeyGenerator
{
    public static VapidKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var x = Normalize32(parameters.Q.X);
        var y = Normalize32(parameters.Q.Y);
        var d = Normalize32(parameters.D);

        var publicPoint = new byte[65];
        publicPoint[0] = 0x04;
        Buffer.BlockCopy(x, 0, publicPoint, 1, 32);
        Buffer.BlockCopy(y, 0, publicPoint, 33, 32);

        return new VapidKeyPair(Encode(publicPoint), Encode(d));
    }

    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Normalize32(byte[]? value)
    {
        if (value is null || value.Length == 0 || value.Length > 32)
            throw new CryptographicException("P-256 key material has an invalid length.");
        if (value.Length == 32) return value;

        var normalized = new byte[32];
        Buffer.BlockCopy(value, 0, normalized, 32 - value.Length, value.Length);
        return normalized;
    }
}
