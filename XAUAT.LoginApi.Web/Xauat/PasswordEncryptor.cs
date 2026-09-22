using System.Security.Cryptography;
using System.Text;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 密码加密与哈希。
/// <para>
/// UAAP 的密码加密不是 RSA 而是自定义的 **AES-CBC/PKCS7**：
/// key = 页面上的 <c>#pwdEncryptSalt</c>（16 个 ASCII 字符，即 16 字节），
/// iv = 从 <see cref="XauatConstants.AesChars"/> 随机取 16 字符，
/// 明文 = 随机 64 字符前缀 + 真实密码，最后 Base64。
/// 那 64 字符前缀是服务端的防重放填充，必须原样带上。
/// </para>
/// </summary>
internal static class PasswordEncryptor
{
    /// <summary>
    /// 加密密码。与 Flask 的 <c>encrypt_aes</c> + <c>encrypt_password</c> 等价：
    /// <para>
    /// 盐值为空（登录页没有该输入框，或 <c>value=""</c>）时**原样返回明文**——这是上游既有行为，
    /// 本服务刻意保留以维持一致，但会打日志让它在运维侧可见。
    /// </para>
    /// <para>
    /// 加密过程本身失败时同样回退明文（Flask 的 <c>except</c> 分支就是这么写的）。
    /// </para>
    /// </summary>
    public static string Encrypt(string password, string? salt, ILogger logger)
    {
        if (string.IsNullOrEmpty(salt))
        {
            logger.LogWarning("登录页未提供加密盐值，将按上游既有行为发送明文密码");
            return password;
        }

        try
        {
            return EncryptWith(password, salt, RandomString(16), RandomString(64));
        }
        catch (Exception ex)
        {
            // Flask: except Exception -> logger.warning("密码加密失败，使用明文密码") -> return password
            logger.LogWarning(ex, "密码加密失败，回退为明文密码");
            return password;
        }
    }

    /// <summary>
    /// 可注入 IV 与前缀的加密实现，仅用于测试确定性。
    /// </summary>
    /// <param name="iv">16 字符的初始向量。</param>
    /// <param name="prefix">64 字符的随机前缀。</param>
    internal static string EncryptWith(string password, string salt, string iv, string prefix)
    {
        var key = Encoding.UTF8.GetBytes(salt);

        // AES 只接受 16/24/32 字节的密钥。Python 在同样情况下会抛 ValueError，
        // 被 encrypt_password 捕获后回退明文——所以这里抛出去、由 Encrypt 兜底，行为一致。
        if (key.Length is not (16 or 24 or 32))
        {
            throw new InvalidOperationException(
                $"加密盐值必须是 16/24/32 字节，实际 {key.Length} 字节");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = Encoding.UTF8.GetBytes(iv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var plaintext = Encoding.UTF8.GetBytes(prefix + password);

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        return Convert.ToBase64String(ciphertext);
    }

    /// <summary>
    /// SHA-256 十六进制小写摘要。
    /// 用于缓存键里的密码哈希（<b>永不落明文</b>）——与 Python 的
    /// <c>hashlib.sha256(pw.encode('utf-8')).hexdigest()</c> 等价。
    /// </summary>
    public static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>从 <see cref="XauatConstants.AesChars"/> 随机取 <paramref name="length"/> 个字符。</summary>
    internal static string RandomString(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = XauatConstants.AesChars[RandomNumberGenerator.GetInt32(XauatConstants.AesChars.Length)];
        }

        return new string(chars);
    }
}
