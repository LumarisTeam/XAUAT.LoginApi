using Microsoft.Extensions.Options;
using XAUAT.LoginApi.Configuration;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Services;

/// <summary>
/// 测试账号识别。用于在没有真实学号密码的情况下跑通全链路（本地联调、AOT 冒烟、端到端测试）。
/// 默认关闭，需显式 <c>TEST_ACCOUNT_ENABLED=true</c>。
/// </summary>
internal interface ITestAccountResolver
{
    /// <summary>用户名与密码是否都命中配置的测试账号。</summary>
    bool IsTestLogin(string username, string password);

    /// <summary>为测试账号伪造一份教务 cookie，形状与真实登录一致。</summary>
    string CreateCookieString();

    /// <summary>伪造的 cookie 是否属于测试账号（用于后续请求识别）。</summary>
    bool IsTestCookie(string cookies);
}

internal sealed class TestAccountResolver(IOptions<TestAccountOptions> options) : ITestAccountResolver
{
    private readonly TestAccountOptions _options = options.Value;

    public bool IsTestLogin(string username, string password)
        => _options.Enabled
           && !string.IsNullOrEmpty(_options.Username)
           && string.Equals(username, _options.Username, StringComparison.Ordinal)
           && string.Equals(password, _options.Password, StringComparison.Ordinal);

    /// <summary>
    /// 形状与 EduApi 的 <c>TestAccountResolver.CreateLoginResponse</c> 保持一致，
    /// 这样同一套前端测试桩在两个服务间可以互换。
    /// </summary>
    public string CreateCookieString()
        => $"__pstsid__={_options.CookieMarker}; SESSION={_options.CookieMarker};";

    public bool IsTestCookie(string cookies)
        => _options.Enabled
           && !string.IsNullOrEmpty(_options.CookieMarker)
           && cookies.Contains(_options.CookieMarker, StringComparison.Ordinal);
}

/// <summary>测试账号的空实现，用于未启用时的注册（避免业务代码到处判空）。</summary>
internal sealed class NoOpTestAccountResolver : ITestAccountResolver
{
    public static readonly NoOpTestAccountResolver Instance = new();

    public bool IsTestLogin(string username, string password) => false;

    public string CreateCookieString() => "";

    public bool IsTestCookie(string cookies) => false;

    private NoOpTestAccountResolver()
    {
    }
}
