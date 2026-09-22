using System.Net;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 会话化的 HTTP 发送：按 host 附加 cookie、手工跟随重定向并逐跳记录。
/// <para>
/// 两个客户端（认证、教务）共用同一套语义，因为它们面对的是同一个 <see cref="XauatCookieJar"/>：
/// 换票时教务会话正是在跳转链的某一跳种下的，必须逐跳可见。
/// </para>
/// </summary>
internal static class XauatHttpSession
{
    /// <summary>重定向跳数上限，防止上游配置错误导致死循环。</summary>
    public const int MaxRedirects = 10;

    /// <summary>
    /// 发送单个请求（不跟随重定向），并按 host 带上 cookie。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var cookieHeader = jar.CookieHeaderFor(request.RequestUri!.Host);
        if (cookieHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var response = await client.SendAsync(request, cancellationToken);
        jar.Capture(request.RequestUri.Host, response);
        return response;
    }

    /// <summary>
    /// 发送并手工跟随重定向，返回**每一跳**的响应（最后一跳是最终响应）。
    /// <para>
    /// 不交给 <c>AllowAutoRedirect</c> 的原因：自动跟随会丢掉中间响应，而换票逻辑
    /// 需要知道"哪一跳种下了教务会话 cookie"；同时手工跟随才能按 host 精确控制 cookie。
    /// </para>
    /// </summary>
    public static async Task<List<HttpResponseMessage>> SendFollowingRedirectsAsync(
        HttpClient client, HttpRequestMessage request, XauatCookieJar jar,
        ILogger logger, CancellationToken cancellationToken)
    {
        var responses = new List<HttpResponseMessage>();
        var current = request;
        var currentUri = request.RequestUri!;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var currentHost = currentUri.Host;
            var cookieHeader = jar.CookieHeaderFor(currentHost);
            if (cookieHeader.Length > 0)
            {
                current.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await client.SendAsync(current, cancellationToken);
            responses.Add(response);
            jar.Capture(currentHost, response);

            var location = response.Headers.Location;
            if (!IsRedirect(response.StatusCode) || location is null)
            {
                return responses;
            }

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            current = new HttpRequestMessage(HttpMethod.Get, currentUri);
        }

        logger.LogWarning("跟随重定向超过 {Max} 跳，已停止", MaxRedirects);
        return responses;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
