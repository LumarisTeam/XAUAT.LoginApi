using XAUAT.LoginApi.Ops;

namespace XAUAT.LoginApi.Tests.Ops;

public class InMemoryLogStoreTests
{
    private static LogRecord Record(string level, string message, string? source = null) => new()
    {
        Timestamp = "2026-09-22T12:00:00+0800",
        Level = level,
        Message = message,
        Source = source
    };

    [Fact]
    public void Add_ShouldEvictOldestWhenCapacityExceeded()
    {
        var store = new InMemoryLogStore();

        for (var i = 0; i < InMemoryLogStore.Capacity + 5; i++)
        {
            store.Add(Record(LogLevels.Information, $"msg-{i}"));
        }

        var (total, items) = store.Query(1, InMemoryLogStore.Capacity, null, null);

        Assert.Equal(InMemoryLogStore.Capacity, total);
        // 最新在前
        Assert.Equal($"msg-{InMemoryLogStore.Capacity + 4}", items[0].Message);
        Assert.DoesNotContain(items, item => item.Message == "msg-0");
    }

    [Fact]
    public void Query_ShouldReturnNewestFirst()
    {
        var store = new InMemoryLogStore();
        store.Add(Record(LogLevels.Information, "first"));
        store.Add(Record(LogLevels.Information, "second"));

        var (_, items) = store.Query(1, 10, null, null);

        Assert.Equal("second", items[0].Message);
        Assert.Equal("first", items[1].Message);
    }

    [Fact]
    public void Query_ShouldFilterByMinimumSeverity()
    {
        // Flask 的 level 过滤是"最低严重度"而非精确匹配
        var store = new InMemoryLogStore();
        store.Add(Record(LogLevels.Debug, "d"));
        store.Add(Record(LogLevels.Information, "i"));
        store.Add(Record(LogLevels.Warning, "w"));
        store.Add(Record(LogLevels.Error, "e"));

        var (total, items) = store.Query(1, 10, LogLevels.Warning, null);

        Assert.Equal(2, total);
        Assert.All(items, item => Assert.Contains(item.Level, new[] { LogLevels.Warning, LogLevels.Error }));
    }

    [Fact]
    public void Query_ShouldSearchMessageSourceAndExceptionCaseInsensitively()
    {
        var store = new InMemoryLogStore();
        store.Add(Record(LogLevels.Information, "登录失败", "AuthService"));
        store.Add(new LogRecord
        {
            Timestamp = "t", Level = LogLevels.Error, Message = "boom",
            Source = "Other", Exception = "NullReferenceException"
        });

        Assert.Equal(1, store.Query(1, 10, null, "authservice").Total);
        Assert.Equal(1, store.Query(1, 10, null, "登录").Total);
        Assert.Equal(1, store.Query(1, 10, null, "nullreference").Total);
        Assert.Equal(0, store.Query(1, 10, null, "nothing-matches").Total);
    }

    [Fact]
    public void Query_ShouldPageTheFilteredSet()
    {
        var store = new InMemoryLogStore();
        for (var i = 0; i < 25; i++) store.Add(Record(LogLevels.Information, $"msg-{i}"));

        var (total, firstPage) = store.Query(1, 10, null, null);
        var (_, thirdPage) = store.Query(3, 10, null, null);

        Assert.Equal(25, total);
        Assert.Equal(10, firstPage.Count);
        Assert.Equal(5, thirdPage.Count);
    }

    [Fact]
    public void LoadFromFiles_ShouldParseFlaskFormatAndMapLevelNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"loginapi-logs-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "log.log"), [
                "[2026-09-22T10:00:00+0800 INFO] app.services.auth: 登录成功",
                "[2026-09-22T10:00:01+0800 WARNING] app.services.xauat: 会话失效",
                "[2026-09-22T10:00:02+0800 CRITICAL] app: 崩了",
                "这行不是日志，应被跳过"
            ]);

            var store = new InMemoryLogStore();
            store.LoadFromFiles(directory);

            var (total, items) = store.Query(1, 10, null, null);
            Assert.Equal(3, total);

            // 文件里是 Python 风格级别名，入内存后要变成 Flask 的 .NET 风格名
            Assert.Equal(LogLevels.Fatal, items[0].Level);
            Assert.Equal(LogLevels.Warning, items[1].Level);
            Assert.Equal(LogLevels.Information, items[2].Level);
            Assert.Equal("app.services.auth", items[2].Source);
            Assert.Equal("登录成功", items[2].Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadFromFiles_ShouldNotThrowWhenDirectoryMissing()
    {
        var store = new InMemoryLogStore();

        store.LoadFromFiles(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}"));

        Assert.Equal(0, store.Query(1, 10, null, null).Total);
    }

    [Fact]
    public void LogFileWriter_ShouldWriteWithoutBomAndSurviveRoundTrip()
    {
        // 两个必须同时成立的性质：
        //   1) 文件首字节不能是 UTF-8 BOM —— 否则第一行永远匹配不上回填正则，重启即静默丢一条
        //   2) 写进去的行能被 LoadFromFiles 原样读回来
        var directory = Path.Combine(Path.GetTempPath(), $"loginapi-roundtrip-{Guid.NewGuid()}");
        try
        {
            var writer = new LogFileWriter(directory);
            var written = "[2026-09-22T10:00:00+0800 INFO] App.Foo: 第一行日志";
            var second = "[2026-09-22T10:00:01+0800 ERROR] App.Bar: 第二行日志";
            writer.Append(written);
            writer.Append(second);

            var bytes = File.ReadAllBytes(Path.Combine(directory, "log.log"));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "日志文件不应带 UTF-8 BOM");

            var store = new InMemoryLogStore();
            store.LoadFromFiles(directory);

            var (total, items) = store.Query(1, 10, null, null);
            Assert.Equal(2, total);
            // 第一行没有被 BOM 顶掉
            Assert.Equal("第一行日志", items[1].Message);
            Assert.Equal("第二行日志", items[0].Message);
            Assert.Equal(LogLevels.Error, items[0].Level);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LogLevels_ShouldMapCriticalToFatal()
    {
        // Flask 的 LEVELS 里最高级叫 Fatal（对应 Python 的 CRITICAL）
        Assert.Equal(LogLevels.Fatal, LogLevels.FromLogLevel(Microsoft.Extensions.Logging.LogLevel.Critical));
        Assert.Equal(LogLevels.Information, LogLevels.FromLogLevel(Microsoft.Extensions.Logging.LogLevel.Information));
        Assert.Equal("CRITICAL", LogLevels.ToFileLevelName(Microsoft.Extensions.Logging.LogLevel.Critical));
        Assert.Equal("INFO", LogLevels.ToFileLevelName(Microsoft.Extensions.Logging.LogLevel.Information));
    }

    [Fact]
    public void LogLevels_ShouldExposeExactlyTheSixDocumentedLevels()
    {
        // 400 文案里列的就是这六个，改了这个数组就要同步改文案
        Assert.Equal(["Trace", "Debug", "Information", "Warning", "Error", "Fatal"], LogLevels.All);
    }
}
