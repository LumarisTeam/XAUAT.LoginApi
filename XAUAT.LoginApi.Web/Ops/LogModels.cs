using Microsoft.Extensions.Logging;

namespace XAUAT.LoginApi.Ops;

/// <summary>
/// 一条日志记录。<see cref="Level"/> 用的是 .NET 风格的名字（Information/Warning/Fatal…），
/// 而不是文件里的 Python 风格（INFO/WARNING/CRITICAL）——这是 Flask 的既有约定：
/// 它的 <c>LogStore.LEVELS</c> 键就是 .NET 命名，<c>/Logs</c> 也按这个名字返回。
/// </summary>
internal sealed class LogRecord
{
    /// <summary>ISO 时间戳带偏移，如 <c>2026-09-22T12:34:56+0800</c>。</summary>
    public string Timestamp { get; init; } = "";

    /// <summary>.NET 风格级别名。</summary>
    public string Level { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>日志来源（<c>ILogger</c> 的 category 名）。</summary>
    public string? Source { get; init; }

    public string? Exception { get; init; }
}

/// <summary>级别名与严重度的换算。数值与 Flask 的 <c>LogStore.LEVELS</c> 逐字一致。</summary>
internal static class LogLevels
{
    public const string Trace = "Trace";
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Fatal = "Fatal";

    /// <summary><c>/Logs?level=</c> 的合法取值（也是 400 文案里列出的那一串）。</summary>
    public static readonly string[] All = [Trace, Debug, Information, Warning, Error, Fatal];

    private static readonly Dictionary<string, int> Severity = new(StringComparer.Ordinal)
    {
        [Trace] = 0,
        [Debug] = 10,
        [Information] = 20,
        [Warning] = 30,
        [Error] = 40,
        [Fatal] = 50
    };

    /// <summary>该级别名是否合法。</summary>
    public static bool IsKnown(string level) => Severity.ContainsKey(level);

    /// <summary>严重度数值；未知级别返回 -1。</summary>
    public static int GetSeverity(string level) => Severity.GetValueOrDefault(level, -1);

    /// <summary>把 <see cref="LogLevel"/> 映射成 Flask 的级别名。</summary>
    public static string FromLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => Trace,
        LogLevel.Debug => Debug,
        LogLevel.Information => Information,
        LogLevel.Warning => Warning,
        LogLevel.Error => Error,
        LogLevel.Critical => Fatal,
        _ => Information
    };

    /// <summary>
    /// 日志**文件**里写的级别名，用 Python 风格。
    /// 这样历史 <c>log.log</c> 文件仍能被本服务的启动加载器解析（格式与 Flask 完全一致）。
    /// </summary>
    public static string ToFileLevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO"
    };

    /// <summary>文件里的级别名 → Flask 的级别名。用于启动时回填历史日志。</summary>
    public static string FromFileLevelName(string fileLevelName) => fileLevelName switch
    {
        "DEBUG" => Debug,
        "INFO" => Information,
        "WARNING" => Warning,
        "ERROR" => Error,
        "CRITICAL" => Fatal,
        _ => Information
    };
}
