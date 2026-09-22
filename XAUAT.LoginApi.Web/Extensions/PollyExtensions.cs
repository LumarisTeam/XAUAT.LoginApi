using Polly;
using Polly.Extensions.Http;

namespace XAUAT.LoginApi.Extensions;

/// <summary>
/// Polly 策略扩展方法
/// </summary>
public static class PollyExtensions
{
    /// <summary>
    /// 获取 HTTP 重试策略
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError() // 处理瞬时 HTTP 错误（5xx 和 408）
            .WaitAndRetryAsync(3, retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))); // 指数退避重试策略
    }

    /// <summary>
    /// 获取超时策略
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(TimeSpan timeout)
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(timeout);
    }
}
