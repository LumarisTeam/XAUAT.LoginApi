using System.Text;

namespace XAUAT.LoginApi.Xauat;

internal static class XauatHttpUtils
{
    /// <summary>
    /// 按响应声明的字符集解码正文，缺省 UTF-8。
    /// <para>
    /// Flask 走的是 <c>requests</c> 的字符集嗅探（先看 Content-Type，再看 meta）。
    /// 这里只认 Content-Type；<c>charset</c> 解析不出来时退回 UTF-8 并**打警告**——
    /// 教务系统是 UTF-8，CAS 页面待第 0 阶段抓到的样本确认，届时若确是 GBK，
    /// 需要引入 <c>System.Text.Encoding.CodePages</c> 并注册 provider。
    /// 打警告是为了让这种情况在日志里立刻可见，而不是表现为"页面文本乱码、正则匹配不上"。
    /// </para>
    /// </summary>
    public static async Task<string> ReadTextAsync(
        HttpResponseMessage response, ILogger logger, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var charset = response.Content.Headers.ContentType?.CharSet;

        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"', '\'', ' ')).GetString(bytes);
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "无法解析响应声明的字符集 {Charset}，已按 UTF-8 解码；若出现乱码需引入 CodePagesEncodingProvider",
                charset);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
