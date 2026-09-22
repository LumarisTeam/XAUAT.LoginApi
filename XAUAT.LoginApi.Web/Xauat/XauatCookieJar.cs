using System.Net.Http.Headers;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 按调用隔离的 cookie 容器。
/// <para>
/// <b>为什么不用 <c>CookieContainer</c>/<c>UseCookies=true</c></b>：那两者的生命周期绑在
/// <c>HttpMessageHandler</c> 上，而 handler 由 <c>IHttpClientFactory</c> 池化复用。
/// 一旦共享，并发的两次登录就会共用一个 cookie 罐——A 同学刚拿到的教务会话会被 B 同学的登录覆盖，
/// 表现为随机串号。Flask 每次 <c>new requests.Session()</c> 天然隔离，这里用
/// "每次登录新建一个 jar + 显式拼 Cookie 头"达到同样效果，同时保留 handler 池化带来的连接复用。
/// </para>
/// <para>
/// cookie 按 host 归属（与 requests 的 cookiejar 行为一致）：authserver 的 JSESSIONID 不会被
/// 发到 swjw，反之亦然。
/// </para>
/// </summary>
internal sealed class XauatCookieJar
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    private readonly record struct Entry(string Host, string Name, string Value);

    /// <summary>把响应里的 <c>Set-Cookie</c> 收进罐子。同名同 host 的覆盖原值并保留原位置。</summary>
    public void Capture(string host, HttpResponseMessage response)
    {
        foreach (var (name, value) in ParseSetCookie(response))
        {
            Put(host, name, value);
        }
    }

    /// <summary>
    /// 只读**这一个响应**种下的 cookie，拼成 <c>"k=v; k=v"</c>。
    /// <para>
    /// 对应 Flask 的 <c>_cookies_to_string(resp.cookies)</c> —— 注意是"该跳响应自己的 cookie"
    /// 而非累积后的会话 cookie。<c>loginFromSSO</c> 取 <c>history[1].cookies</c> 就是这个语义。
    /// </para>
    /// </summary>
    public static string ReadResponseCookies(HttpResponseMessage response)
        => string.Join("; ", ParseSetCookie(response).Select(c => $"{c.Name}={c.Value}"));

    private static List<(string Name, string Value)> ParseSetCookie(HttpResponseMessage response)
    {
        var cookies = new List<(string, string)>();
        if (!response.Headers.TryGetValues("Set-Cookie", out var headerValues)) return cookies;

        foreach (var headerValue in headerValues)
        {
            // 只取 "name=value" 段，丢弃 Path/HttpOnly/Secure/Expires 等属性
            var pair = headerValue.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            // 值可能被双引号包裹（RFC 6265 允许）
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            if (name.Length == 0) continue;

            cookies.Add((name, value));
        }

        return cookies;
    }

    /// <summary>把 <c>"k=v; k=v"</c> 形式的 cookie 串预先塞进罐子（用于复用缓存的 SSO 票据）。</summary>
    public void Seed(string host, string cookieString)
    {
        foreach (var pair in cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (name.Length == 0) continue;

            Put(host, name, value);
        }
    }

    /// <summary>写入一个 cookie；同名同 host 覆盖原值并保留原位置（保序便于与 Flask 的输出对拍）。</summary>
    private void Put(string host, string name, string value)
    {
        var key = $"{host}\u0000{name}";
        if (_index.TryGetValue(key, out var existing))
        {
            _entries[existing] = new Entry(host, name, value);
        }
        else
        {
            _index[key] = _entries.Count;
            _entries.Add(new Entry(host, name, value));
        }
    }

    /// <summary>
    /// 属于 <paramref name="host"/> 的 cookie，拼成 <c>Cookie</c> 头。
    /// 同时接受精确匹配与域后缀匹配（<c>.xauat.edu.cn</c> 这类带点前缀的域）。
    /// </summary>
    public string CookieHeaderFor(string host)
        => string.Join("; ", _entries
            .Where(entry => Matches(entry.Host, host))
            .Select(entry => $"{entry.Name}={entry.Value}"));

    /// <summary>
    /// 全部 cookie 的 <c>"k=v; k=v"</c> 串（跨 host，按写入顺序）。
    /// 对应 Flask 的 <c>_cookies_to_string(self.session.cookies)</c>——登录页返回 200 的
    /// 成功分支就是用它把整个会话 cookie 串出来的。
    /// </summary>
    public string AllAsString()
        => string.Join("; ", _entries.Select(entry => $"{entry.Name}={entry.Value}"));

    /// <summary>罐内是否有该 host 的 cookie。</summary>
    public bool HasCookiesFor(string host)
        => _entries.Any(entry => Matches(entry.Host, host));

    private static bool Matches(string cookieHost, string host)
        => string.Equals(cookieHost, host, StringComparison.OrdinalIgnoreCase)
           || (cookieHost.StartsWith('.') &&
               host.EndsWith(cookieHost, StringComparison.OrdinalIgnoreCase));
}
