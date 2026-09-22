using System.Globalization;

namespace XAUAT.LoginApi.Ops;

/// <summary>
/// 把日志同时送进内存环形缓冲（供 <c>/Logs</c> 查询）与按天轮转的文件。
/// <para>
/// <b>刻意不清空默认的日志提供程序</b>：部署脚本（<c>build.sh</c> / CI 的 deploy job）
/// 靠容器日志里的 <c>Now listening on</c> / <c>Application started</c> 判断服务是否就绪，
/// 清掉控制台日志会让就绪检测一直等到超时。因此控制台维持 .NET 原生格式，
/// 而 <c>/Logs</c> 与日志文件用 Flask 兼容格式——两者互不影响。
/// </para>
/// </summary>
internal sealed class OpsLoggerProvider(InMemoryLogStore store, LogFileWriter? fileWriter) : ILoggerProvider
{
    /// <summary>
    /// 入库与落盘的最低级别。Flask 在 <c>FLASK_ENV=production</c> 下用 INFO，
    /// 这里对齐（即 Debug 只在控制台可见，不进 <c>/Logs</c>）。
    /// </summary>
    private const LogLevel MinimumLevel = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => new OpsLogger(store, fileWriter, categoryName);

    public void Dispose()
    {
    }

    private sealed class OpsLogger(InMemoryLogStore store, LogFileWriter? fileWriter, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var exceptionText = exception?.ToString();

            var record = new LogRecord
            {
                Timestamp = FormatTimestamp(DateTimeOffset.Now),
                Level = LogLevels.FromLogLevel(logLevel),
                Message = message,
                Source = categoryName,
                Exception = exceptionText
            };

            store.Add(record);

            // 文件格式与 Flask 逐字一致：'[%(asctime)s %(levelname)s] %(name)s: %(message)s'
            // 级别名用 Python 风格，这样历史文件与新文件都能被同一个加载器解析。
            var line = $"[{record.Timestamp} {LogLevels.ToFileLevelName(logLevel)}] {categoryName}: {message}";
            fileWriter?.Append(line);
        }
    }

    /// <summary>
    /// 复刻 Python <c>datefmt='%Y-%m-%dT%H:%M:%S%z'</c> 的形态：
    /// <c>2026-09-22T12:34:56+0800</c>——注意偏移**不带冒号**（.NET 的 <c>zzz</c> 会带）。
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";

        return string.Create(CultureInfo.InvariantCulture,
            $"{value:yyyy-MM-ddTHH:mm:ss}{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}");
    }
}
