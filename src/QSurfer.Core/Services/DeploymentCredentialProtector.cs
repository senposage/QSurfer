using System.Security.Cryptography;
using System.Text;

namespace QSurfer.Core.Services;

/// <summary>
/// Prevents a shared deployment credential from being readable in config.json.
/// This is intentionally obfuscation against casual inspection, not a vault:
/// the application contains the key needed to use the credential.
/// </summary>
public static class DeploymentCredentialProtector
{
    private static readonly byte[] LocalEnvelopeHeader = "QSL1"u8.ToArray();
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("QSurfer deployment credential key v1 3A0DE19AFB5B74D2"));
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("QSurfer:deployment-password:v1");

    public static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "";
        }

        return "v1:" + Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(password), AssociatedData));
    }

    public static string Unprotect(string protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            return "";
        }
        if (!protectedPassword.StartsWith("v1:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The shared deployment credential has an unsupported format.");
        }

        try
        {
            return Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(protectedPassword[3..]), AssociatedData));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The shared deployment credential has an unsupported format.", ex);
        }
    }

    public static byte[] ProtectLocalPayload(byte[] payload)
    {
        var encryptedPayload = ProtectBytes(payload, Encoding.UTF8.GetBytes("QSurfer:local-password:v1"));
        var envelope = new byte[LocalEnvelopeHeader.Length + encryptedPayload.Length];
        Buffer.BlockCopy(LocalEnvelopeHeader, 0, envelope, 0, LocalEnvelopeHeader.Length);
        Buffer.BlockCopy(encryptedPayload, 0, envelope, LocalEnvelopeHeader.Length, encryptedPayload.Length);
        return envelope;
    }

    public static bool IsLocalPayload(byte[] payload) =>
        payload.Length > LocalEnvelopeHeader.Length + 28 &&
        payload.AsSpan(0, LocalEnvelopeHeader.Length).SequenceEqual(LocalEnvelopeHeader);

    public static byte[] UnprotectLocalPayload(byte[] payload)
    {
        if (!IsLocalPayload(payload))
        {
            throw new InvalidOperationException("The encrypted local credential has an unsupported format.");
        }
        return UnprotectBytes(payload[LocalEnvelopeHeader.Length..], Encoding.UTF8.GetBytes("QSurfer:local-password:v1"));
    }

    private static byte[] ProtectBytes(byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(Key, tagSizeInBytes: 16);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return payload;
    }

    private static byte[] UnprotectBytes(byte[] payload, byte[] associatedData)
    {
        if (payload.Length <= 28)
        {
            throw new InvalidOperationException("The encrypted credential payload is invalid.");
        }

        var nonce = payload[..12];
        var tag = payload[12..28];
        var ciphertext = payload[28..];
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new AesGcm(Key, tagSizeInBytes: 16);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}
