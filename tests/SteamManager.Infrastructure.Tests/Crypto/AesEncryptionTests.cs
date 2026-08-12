using SteamManager.Infrastructure.Crypto;
using Xunit;

namespace SteamManager.Infrastructure.Tests.Crypto;

public class AesEncryptionTests
{
    private const string Key = "this-is-a-32-char-test-key-12345";

    [Fact]
    public void Encrypt_ThenDecrypt_ReturnsOriginal()
    {
        var plaintext = "super-secret-token-value";
        var encrypted = AesEncryption.Encrypt(plaintext, Key);
        var decrypted = AesEncryption.Decrypt(encrypted, Key);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var a = AesEncryption.Encrypt("same-input", Key);
        var b = AesEncryption.Encrypt("same-input", Key);
        Assert.NotEqual(a, b); // random IV
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsException()
    {
        var encrypted = AesEncryption.Encrypt("data", Key);
        Assert.ThrowsAny<Exception>(() =>
            AesEncryption.Decrypt(encrypted, "wrong-key-00000000000000000000000"));
    }

    [Fact]
    public void Encrypt_EmptyInputs_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => AesEncryption.Encrypt("", Key));
        Assert.Throws<ArgumentException>(() => AesEncryption.Encrypt("data", ""));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsInsteadOfReturningCorruptedData()
    {
        var encrypted = AesEncryption.Encrypt("integrity-check", Key);
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0xFF; // flip a byte in the ciphertext
        var tampered = Convert.ToBase64String(bytes);
        Assert.ThrowsAny<Exception>(() => AesEncryption.Decrypt(tampered, Key));
    }
}
