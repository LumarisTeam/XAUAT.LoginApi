using XAUAT.LoginApi.Configuration;

namespace XAUAT.LoginApi.Extensions;

/// <summary>
/// 服务配置类。
/// 本服务不碰数据库（Flask 侧也没有 DB），因此没有 <c>SqlConnectionString</c>；
/// 也不提供 Prometheus / Logging 开关——日志走内置 Logging，指标由部署层采集。
/// </summary>
public class ServiceConfiguration
{
    /// <summary>
    /// Redis 连接字符串。留空表示不启用 Redis。
    /// <para>
    /// 与 Flask 的行为差异：Flask 在 Redis 不可用时**运行时降级**（直接走全量登录，无缓存无限流），
    /// 这里同样保留该降级路径，但"配了地址却连不上"是**启动期失败**（abortConnect 默认 true）。
    /// 即：没配 = 降级，配错 = 启动就炸，避免配置错误被静默掩盖。
    /// </para>
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// <c>/Logs</c> 的访问令牌。留空表示该接口开放访问（与 Flask 一致）。
    /// </summary>
    public string? LogViewToken { get; set; }

    /// <summary>
    /// <c>/Logs</c> 的额外跨域来源，逗号分隔。
    /// 内置白名单见 <see cref="Endpoints.OpsEndpoints"/>；这里是 Flask 的 CORS_ALLOWED_ORIGINS。
    /// </summary>
    public string? CorsAllowedOrigins { get; set; }

    /// <summary>
    /// 测试账号配置
    /// </summary>
    public TestAccountOptions TestAccount { get; set; } = new();

    /// <summary>
    /// 从环境变量中创建 ServiceConfiguration 实例
    /// </summary>
    public static ServiceConfiguration FromEnvironment()
    {
        return new ServiceConfiguration
        {
            RedisConnectionString = EnvironmentVariableHelper.GetString("REDIS", "Redis"),
            LogViewToken = EnvironmentVariableHelper.GetString("LOG_VIEW_TOKEN", "LogView__Token"),
            CorsAllowedOrigins = EnvironmentVariableHelper.GetString("CORS_ALLOWED_ORIGINS", "Cors__AllowedOrigins"),
            TestAccount = EnvironmentVariableHelper.BuildTestAccountOptions()
        };
    }
}
