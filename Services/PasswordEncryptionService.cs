using System;
using System.Security.Cryptography;
using System.Text;

namespace ChiXueSsh.Services;

public static class PasswordEncryptionService
{
    private const string ProtectedPrefix = "cxaes:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("CxShell.Session.Password.v1"));

    public static string Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherText = new byte[bytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, bytes, cipherText, tag);

        var payload = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, payload, nonce.Length + tag.Length, cipherText.Length);
        return ProtectedPrefix + Convert.ToBase64String(payload);
    }

    public static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        if (!cipherText.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return cipherText;

        try
        {
            var payload = Convert.FromBase64String(cipherText[ProtectedPrefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
                return string.Empty;

            var nonce = payload[..NonceSize];
            var tag = payload[NonceSize..(NonceSize + TagSize)];
            var encrypted = payload[(NonceSize + TagSize)..];
            var plain = new byte[encrypted.Length];

            using var aes = new AesGcm(Key, TagSize);
            aes.Decrypt(nonce, encrypted, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool HasSavedPassword(string? cipherText)
        => !string.IsNullOrEmpty(Decrypt(cipherText));
}
