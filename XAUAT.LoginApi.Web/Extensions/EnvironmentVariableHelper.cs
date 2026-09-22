using XAUAT.LoginApi.Configuration;

namespace XAUAT.LoginApi.Extensions;

/// <summary>
/// 环境变量读取辅助。
/// 只保留登录服务需要的部分——EduApi 版本里的 Electricity / SMTP / MapAdmin / Serilog 构建方法
/// 均不随本服务迁移。
/// </summary>
public static class EnvironmentVariableHelper
{
    public static string? GetString(params string[] variableNames)
    {
        return variableNames
            .Select(variableName => Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    public static string GetStringOrDefault(string defaultValue, params string[] variableNames)
    {
        return GetString(variableNames) ?? defaultValue;
    }

    public static int GetIntOrDefault(int defaultValue, params string[] variableNames)
    {
        var value = GetString(variableNames);
        return int.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
    }

    public static bool GetBoolOrDefault(bool defaultValue, params string[] variableNames)
    {
        var value = GetString(variableNames);
        return bool.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
    }

    public static TestAccountOptions BuildTestAccountOptions()
    {
        return new TestAccountOptions
        {
            Enabled = GetBoolOrDefault(false, "TEST_ACCOUNT_ENABLED", "TestAccount__Enabled"),
            Username = GetStringOrDefault("frontend-test", "TEST_ACCOUNT_USERNAME", "TestAccount__Username"),
            Password = GetStringOrDefault("frontend-test-password", "TEST_ACCOUNT_PASSWORD", "TestAccount__Password"),
            StudentId = GetStringOrDefault("20239999", "TEST_ACCOUNT_STUDENT_ID", "TestAccount__StudentId"),
            CookieMarker = GetStringOrDefault("frontend-test-marker", "TEST_ACCOUNT_COOKIE_MARKER",
                "TestAccount__CookieMarker"),
            FixturePath = GetStringOrDefault("TestFixtures", "TEST_ACCOUNT_FIXTURE_PATH", "TestAccount__FixturePath")
        };
    }
}
