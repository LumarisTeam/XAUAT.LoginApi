using System.Text.RegularExpressions;

namespace XAUAT.LoginApi.Ops;

/// <summary>
/// 进程内的环形日志缓冲，供 <c>/Logs</c> 查询。
/// <para>
/// 容量 2000 条（Flask 的 <c>LogStore(capacity=2000)</c>，超限丢最旧的）。
/// </para>
/// </summary>
internal sealed partial class InMemoryLogStore
{
    public const int Capacity = 2000;

    private readonly Queue<LogRecord> _entries = new(Capacity);
    private readonly Lock _gate = new();

    /// <summary>
    /// 解析历史日志文件用。与 Flask <c>LogStore</c> 的正则逐字一致，
    /// 这样旧部署留下的 <c>logs/log.log*</c> 能被新服务继续读出。
    /// </summary>
    [GeneratedRegex(@"^\[(?<time>[^ ]+) (?<level>\w+)\] (?<source>[^:]+): (?<message>.*)$")]
    private static partial Regex LogLineRegex();

    public void Add(LogRecord record)
    {
        lock (_gate)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(record);
        }
    }

    /// <summary>
    /// 分页查询，最新在前。
    /// </summary>
    /// <param name="page">1 起。</param>
    /// <param name="pageSize">条数。</param>
    /// <param name="minLevel">最低严重度（含），null 表示不限。</param>
    /// <param name="search">关键词，大小写不敏感，匹配 message/source/exception 的拼接。</param>
    public (int Total, List<LogRecord> Items) Query(int page, int pageSize, string? minLevel, string? search)
    {
        List<LogRecord> snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        IEnumerable<LogRecord> filtered = snapshot;

        if (!string.IsNullOrEmpty(minLevel) && LogLevels.IsKnown(minLevel))
        {
            var threshold = LogLevels.GetSeverity(minLevel);
            filtered = filtered.Where(record => LogLevels.GetSeverity(record.Level) >= threshold);
        }

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(record =>
                BuildSearchText(record).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var matches = filtered.Reverse().ToList();

        var items = matches
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (matches.Count, items);
    }

    /// <summary>搜索目标与 Flask 一致：message、source、exception 三段拼接。</summary>
    private static string BuildSearchText(LogRecord record)
        => $"{record.Message} {record.Source} {record.Exception}";

    /// <summary>
    /// 启动时从 <paramref name="directory"/> 下的 <c>log.log*</c> 回填历史日志
    /// （Flask 的 <c>_load_existing_files</c>）：按文件名倒序读，解析不出来的行跳过。
    /// </summary>
    public void LoadFromFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, "log.log*");
        Array.Sort(files, StringComparer.Ordinal);
        Array.Reverse(files);

        var loaded = 0;
        foreach (var file in files)
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var match = LogLineRegex().Match(line);
                    if (!match.Success) continue;

                    Add(new LogRecord
                    {
                        Timestamp = match.Groups["time"].Value,
                        Level = LogLevels.FromFileLevelName(match.Groups["level"].Value),
                        Source = match.Groups["source"].Value,
                        Message = match.Groups["message"].Value
                    });
                    loaded++;
                }
            }
            catch (IOException)
            {
                // 读不到就跳过——日志回填失败不该阻止服务启动
            }
        }

        if (loaded > 0)
        {
            Console.WriteLine($"[startup] 已回填 {loaded} 条历史日志（{files.Length} 个文件）");
        }
    }
}
