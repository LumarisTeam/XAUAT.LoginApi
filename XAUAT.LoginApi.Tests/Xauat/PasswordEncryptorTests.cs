using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.LoginApi.Tests.TestSupport;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

public class PasswordEncryptorTests
{
    private const string Salt = "rjBFAaHsNkKAhpoi";
    private const string Iv = "abcdefghijkmnpqr";
    private const string Prefix = "ABCDEFGHJKMNPQRSabcdefghijkmnprst2345678ABCDEFGHJKMNPQRSabcdef";

    [Fact]
    public void EncryptWith_ShouldRoundTripToPrefixPlusPassword()
    {
        var encrypted = PasswordEncryptor.EncryptWith("my-password", Salt, Iv, Prefix);

        var decrypted = Decrypt(encrypted, Salt, Iv);

        // 上游要求明文 = 64 字符随机前缀 + 真实密码，前缀必须原样带上
        Assert.Equal(Prefix + "my-password", decrypted);
    }

    [Fact]
    public void EncryptWith_ShouldProduceBase64ThatDoesNotContainPlaintext()
    {
        var encrypted = PasswordEncryptor.EncryptWith("my-password", Salt, Iv, Prefix);

        Assert.DoesNotContain("my-password", encrypted, StringComparison.Ordinal);
        Assert.True(Convert.TryFromBase64String(encrypted, new byte[encrypted.Length], out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Encrypt_ShouldFallBackToPlaintextWhenSaltMissing(string? salt)
    {
        // Flask: encrypt_aes 的 `if f:` 为假时原样返回；salt_input 缺失 -> None，
        // 存在但 value="" -> ""。两者都必须走明文，这是上游既有行为。
        var result = PasswordEncryptor.Encrypt("my-password", salt, NullLogger.Instance);

        Assert.Equal("my-password", result);
    }

    [Fact]
    public void Encrypt_ShouldFallBackToPlaintextWhenSaltIsNotAValidKeySize()
    {
        // 上游若给出非 16/24/32 字节的盐，Python 会抛 ValueError 并被 encrypt_password 吞掉 -> 明文。
        // 这里必须保持同样的兜底，且不能把登录打挂。
        var result = PasswordEncryptor.Encrypt("my-password", "short", NullLogger.Instance);

        Assert.Equal("my-password", result);
    }

    [Fact]
    public void Encrypt_ShouldEncryptWhenSaltIsValid()
    {
        var result = PasswordEncryptor.Encrypt("my-password", Salt, NullLogger.Instance);

        Assert.NotEqual("my-password", result);
        // 输入是 64 字符前缀 + 9 字符密码 = 73 字节，PKCS7 补齐到 80 字节 -> Base64 长 108 左右
        Assert.True(result.Length > 100);
    }

    [Fact]
    public void Sha256Hex_ShouldMatchPythonHashlibOutput()
    {
        // 对齐 hashlib.sha256(b"password").hexdigest()：小写十六进制、64 字符。
        // 这个值直接决定了 Redis 缓存键，算错就会读不到 Flask 写的缓存。
        var hash = PasswordEncryptor.Sha256Hex("password");

        Assert.Equal("5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8", hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void RandomString_ShouldOnlyUseUpstreamAlphabet()
    {
        var value = PasswordEncryptor.RandomString(64);

        Assert.Equal(64, value.Length);
        Assert.All(value, ch => Assert.Contains(ch, XauatConstants.AesChars));
        // 上游字母表刻意去掉了易混淆字符
        Assert.DoesNotContain("I", value, StringComparison.Ordinal);
        Assert.DoesNotContain("l", value, StringComparison.Ordinal);
        Assert.DoesNotContain("0", value, StringComparison.Ordinal);
        Assert.DoesNotContain("1", value, StringComparison.Ordinal);
    }

    private static string Decrypt(string base64Ciphertext, string salt, string iv)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(salt);
        aes.IV = Encoding.UTF8.GetBytes(iv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var ciphertext = Convert.FromBase64String(base64Ciphertext);
        using var decryptor = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length));
    }
}
