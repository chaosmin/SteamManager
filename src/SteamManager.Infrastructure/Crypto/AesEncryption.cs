using System.Security.Cryptography;
using System.Text;

namespace SteamManager.Infrastructure.Crypto;

public static class AesEncryption
{
    public static string Encrypt(string plaintext, string key)
    {
        if (string.IsNullOrEmpty(plaintext)) throw new ArgumentException("plaintext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);
        aes.GenerateIV();

        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        cs.Write(bytes, 0, bytes.Length);
        cs.FlushFinalBlock();
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string ciphertext, string key)
    {
        if (string.IsNullOrEmpty(ciphertext)) throw new ArgumentException("ciphertext");
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");

        var data = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = DeriveKey(key);
        aes.IV = data[..16];

        using var ms = new MemoryStream(data, 16, data.Length - 16);
        using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var reader = new StreamReader(cs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] DeriveKey(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
