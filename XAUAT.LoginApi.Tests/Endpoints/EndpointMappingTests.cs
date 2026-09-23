using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace XAUAT.LoginApi.Tests.Endpoints;

/// <summary>
/// 组合根冒烟测试：确认 <c>Program.cs</c> 真的把每个 <c>Map*Endpoints()</c> 都挂上了。
/// <para>
/// <b>为什么必须有这个测试</b>：本仓库其余测试全是直接 <c>new</c> 服务、解析器、
/// <c>AuthService</c>，<b>没有任何一个经过 <c>Program.cs</c></b>。删掉一行
/// <c>app.MapAuthEndpoints()</c> 的后果是"登录接口整体 404"，而编译、101 个单测、
/// AOT 发布**全都不会报错**——2026-09-23 就是这样漏过一次，靠人工发现。
/// </para>
/// <para>
/// 断言只要求"不是 404"而不是具体状态码：这个测试守的是"路由有没有挂上"，
/// 各端点的业务状态码由它们自己的测试负责。对非法输入返回 400/401/403/429/500
/// 都算挂上了。
/// </para>
/// </summary>
public class EndpointMappingTests
{
    static EndpointMappingTests()
    {
        // Program.cs 在 Build() 之前就读环境变量构造 ServiceConfiguration，而 DotNetEnv
        // 用的是 NoClobber（先设的赢）。这里强制走"未配置 Redis"的降级路径，
        // 让冒烟测试不依赖本机 .env，也不会真去连 Redis。
        Environment.SetEnvironmentVariable("REDIS", "");
    }

    [Theory]
    // AuthEndpoints —— 本轮踩坑的就是这一组
    [InlineData("POST", "/auth/login")]
    [InlineData("GET", "/login/2024001/pw")]
    [InlineData("GET", "/security/ban_status")]
    [InlineData("POST", "/security/ban_status")]
    [InlineData("GET", "/security/ban_logs")]
    // Statistics / Ops / 健康检查
    [InlineData("GET", "/statistics/users")]
    [InlineData("GET", "/user_count")]
    [InlineData("GET", "/Logs")]
    [InlineData("GET", "/health")]
    public async Task Route_ShouldBeMapped(string method, string path)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_ShouldReturnOk()
    {
        // 顺带确认宿主真的起得来：若上面的用例全因为"宿主启动失败"而 500，这条会露馅
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthLogin_ShouldRejectEmptyCredentials()
    {
        // 端点存在的**行为**证据：空凭据必须被业务拒掉，而不是路由没挂上的 404
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var content = new StringContent(
            """{"username":"","password":""}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
