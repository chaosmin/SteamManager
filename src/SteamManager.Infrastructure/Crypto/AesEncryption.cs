using System.Security.Cryptography;
using System.Text;

namespace SteamManager.Infrastructure.Crypto;

/// <summary>
/// AES-256-GCM with PBKDF2 key derivation. Layout: salt(16) || nonce(12) || tag(16) || ciphertext.
/// GCM's tag makes tampering fail decryption instead of silently returning corrupted plaintext.
/// </summary>
public static class AesEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 100_000;

    public static string Encrypt(string plaintext, string key)
    {
        if (string.IsNullOrEmpty(plaintext)) throw new ArgumentException("plaintext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(DeriveKey(key, salt), TagSize))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[SaltSize + NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, result, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, result, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, result, SaltSize + NonceSize + TagSize, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string ciphertext, string key)
    {
        if (string.IsNullOrEmpty(ciphertext)) throw new ArgumentException("ciphertext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        var data = Convert.FromBase64String(ciphertext);
        var salt = data[..SaltSize];
        var nonce = data[SaltSize..(SaltSize + NonceSize)];
        var tag = data[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
        var cipherBytes = data[(SaltSize + NonceSize + TagSize)..];
        var plainBytes = new byte[cipherBytes.Length];

        using (var aes = new AesGcm(DeriveKey(key, salt), TagSize))
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string key, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(key), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
}
