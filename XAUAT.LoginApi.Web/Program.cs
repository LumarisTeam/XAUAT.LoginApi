using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using DotNetEnv;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using XAUAT.LoginApi.Configuration;
using XAUAT.LoginApi.Endpoints;
using XAUAT.LoginApi.Extensions;
using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Ops;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Services;
using XAUAT.LoginApi.Xauat;

LoadDotEnvIfPresent();

var builder = WebApplication.CreateSlimBuilder(args);

// JSON：把源生成上下文接到 Minimal API 的序列化器上（Native AOT 必需）
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, LoginJsonContext.Default);
    // Flask 设了 ensure_ascii=False，中文原样输出；默认编码器会转成 \uXXXX
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// ------------------------------------------------------------------ 配置
var serviceConfig = ServiceConfiguration.FromEnvironment();
builder.Services.AddSingleton(serviceConfig);

// ------------------------------------------------------------------ 日志
// 内存环形缓冲（供 /Logs）+ 按天轮转文件。
// 刻意**不清空**默认提供程序：部署脚本靠容器日志里的 "Now listening on" 判断就绪。
var logDirectory = EnvironmentVariableHelper.GetStringOrDefault("logs", "LOG_DIR", "Logging__Directory");
var logStore = new InMemoryLogStore();
logStore.LoadFromFiles(logDirectory);
builder.Logging.AddProvider(new OpsLoggerProvider(logStore, new LogFileWriter(logDirectory)));
builder.Services.AddSingleton(logStore);

// ------------------------------------------------------------------ Redis
// 两种失败语义刻意区分开：
//   没配 REDIS   -> 不注册，LoginRedisStore 走降级（无缓存、无限流全量登录），与 Flask 一致
//   配了却连不上 -> ConnectionMultiplexer.Connect 直接抛异常，启动即失败，避免配置错误被静默掩盖
if (!string.IsNullOrEmpty(serviceConfig.RedisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(serviceConfig.RedisConnectionString));
}

builder.Services.AddSingleton<ILoginRedisStore, LoginRedisStore>();
builder.Services.AddSingleton<BanService>();

// ------------------------------------------------------------------ 测试账号
builder.Services.AddSingleton(Options.Create(serviceConfig.TestAccount));
if (serviceConfig.TestAccount.Enabled)
{
    builder.Services.AddSingleton<ITestAccountResolver, TestAccountResolver>();
}
else
{
    // 关闭时注册空实现，业务代码就不必到处判空
    builder.Services.AddSingleton<ITestAccountResolver>(NoOpTestAccountResolver.Instance);
}

// ------------------------------------------------------------------ 业务服务
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<StatisticsService>();

// ------------------------------------------------------------------ HTTP 客户端
// 认证服务器：明文 HTTP，且**必须**关掉自动重定向与自动 cookie 管理——
// 换票逻辑要靠"逐跳捕获 Set-Cookie"，自动跟随会把这些中间响应丢掉。
// cookie 由每次调用新建的 XauatCookieJar 显式管理，避免并发登录串号。
builder.Services.AddHttpClient(XauatSsoClient.ClientName)
    .ConfigureHttpClient(client =>
    {
        client.Timeout = XauatConstants.AuthServerTimeout;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", XauatConstants.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", XauatConstants.AcceptHtml);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", XauatConstants.AcceptLanguage);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

// 认证服务器需要会话化调用（先 GET 登录页拿 lt/execution，再 POST），因此单例持有客户端
builder.Services.AddSingleton<IXauatSsoClient, XauatSsoClient>();

// ------------------------------------------------------------------ CORS（仅 /Logs）
builder.Services.AddCors(options => options.AddPolicy(LogsCors.PolicyName, policy => policy
    .SetIsOriginAllowed(origin => LogsCors.IsAllowedOrigin(origin, serviceConfig.CorsAllowedOrigins))
    .WithMethods("GET", "OPTIONS")
    .WithHeaders("Authorization", "Content-Type", "X-Log-Token")
    .AllowCredentials()));

var app = builder.Build();

// 启动横幅：一条日志就能确认线上跑的是哪个变体——AOT 产物里没有 JIT，此值为 false。
// 用 Console 而不是 ILogger：AOT 下结构化日志的参数重载会带来分析器告警
// （csproj 把 IL2026/IL3050 设成了错误），为一行横幅不值当。
Console.WriteLine($"[startup] Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine($"[startup] Redis: {(string.IsNullOrEmpty(serviceConfig.RedisConnectionString) ? "未配置（无缓存模式）" : "已配置")}");
Console.WriteLine($"[startup] 日志目录: {logDirectory}");

// 必须在端点之前：/Logs 用的是 RequireCors(LogsCors.PolicyName)，
// 只注册策略而不挂中间件会在请求时抛 "contains CORS metadata, but a middleware was not found"。
// 其余端点不需要跨域（调用方是服务端与移动端，不走浏览器同源策略）。
app.UseCors();

app.MapGet("/health", () => Results.Text("ok"));

app.MapAuthEndpoints();
app.MapStatisticsEndpoints();
app.MapOpsEndpoints();

app.Run();

void LoadDotEnvIfPresent()
{
    var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (currentDirectory is not null)
    {
        var dotEnvPath = Path.Combine(currentDirectory.FullName, ".env");
        if (File.Exists(dotEnvPath))
        {
            Env.NoClobber().Load(dotEnvPath);
            return;
        }

        currentDirectory = currentDirectory.Parent;
    }
}

/// <summary>供集成测试的 <c>WebApplicationFactory&lt;Program&gt;</c> 使用。</summary>
public partial class Program;
