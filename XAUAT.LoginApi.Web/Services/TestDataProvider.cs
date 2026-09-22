using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using XAUAT.LoginApi.Configuration;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Services;

/// <summary>测试账号的课表，格式与 <see cref="CourseInfo"/> 对应（去掉派生出的解析结果）。</summary>
internal sealed class TestCourseFixture
{
    public string LessonId { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string PersonName { get; set; } = "";
    public string RoomZh { get; set; } = "";

    /// <summary><c>yyyy-MM-dd</c>。</summary>
    public string Date { get; set; } = "";

    /// <summary><c>HHMM</c> 整数，如 <c>800</c> 表示 08:00。</summary>
    public int StartTime { get; set; }

    public int EndTime { get; set; }
}

/// <summary>测试账号的考试安排。</summary>
internal sealed class TestExamFixture
{
    public string Course { get; set; } = "";

    /// <summary><c>2026-06-20 08:00~10:00</c>。</summary>
    public string Time { get; set; } = "";

    public string Room { get; set; } = "";
    public string SeatNo { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<TestCourseFixture>))]
[JsonSerializable(typeof(List<TestExamFixture>))]
internal partial class TestFixtureJsonContext : JsonSerializerContext;

/// <summary>测试账号的数据来源：读 <c>TestFixtures/</c> 下的固定 JSON。</summary>
internal interface ITestDataProvider
{
    IReadOnlyList<CourseInfo> GetCourses();
    IReadOnlyList<ExamInfo> GetExams();
}

/// <summary>
/// 从固定文件读取测试数据。
/// <para>
/// <c>TestFixtures/</c> 与测试项目里的同名目录**用途不同**：这里是随镜像发布的、
/// 供测试账号旁路使用的合成数据；而 <c>XAUAT.LoginApi.Tests/TestFixtures/</c> 放的是
/// 由 <c>tools/capture-fixtures.py</c> 抓取的上游真实样本，只服务于单元测试。
/// </para>
/// </summary>
internal sealed class TestDataProvider(IOptions<TestAccountOptions> options, ILogger<TestDataProvider> logger)
    : ITestDataProvider
{
    private readonly TestAccountOptions _options = options.Value;

    private IReadOnlyList<CourseInfo>? _courses;
    private IReadOnlyList<ExamInfo>? _exams;

    public IReadOnlyList<CourseInfo> GetCourses() => _courses ??= LoadCourses();

    public IReadOnlyList<ExamInfo> GetExams() => _exams ??= LoadExams();

    private IReadOnlyList<CourseInfo> LoadCourses()
    {
        var fixtures = ReadFixture("test-courses.json", TestFixtureJsonContext.Default.ListTestCourseFixture);
        if (fixtures is null) return [];

        var result = new List<CourseInfo>(fixtures.Count);
        foreach (var item in fixtures)
        {
            var parts = item.Date.Split('-');
            if (parts.Length != 3) continue;

            var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var month = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var day = int.Parse(parts[2], CultureInfo.InvariantCulture);

            result.Add(new CourseInfo(
                item.LessonId, item.CourseName, item.PersonName, item.RoomZh, item.Date,
                item.StartTime, item.EndTime,
                new DateTime(year, month, day, item.StartTime / 100, item.StartTime % 100, 0, DateTimeKind.Unspecified),
                new DateTime(year, month, day, item.EndTime / 100, item.EndTime % 100, 0, DateTimeKind.Unspecified)));
        }

        return result;
    }

    private IReadOnlyList<ExamInfo> LoadExams()
    {
        var fixtures = ReadFixture("test-exams.json", TestFixtureJsonContext.Default.ListTestExamFixture);
        if (fixtures is null) return [];

        var result = new List<ExamInfo>(fixtures.Count);
        foreach (var item in fixtures)
        {
            // 与真实解析同格式："2026-06-20 08:00~10:00"
            var spaceIndex = item.Time.IndexOf(' ');
            if (spaceIndex < 0) continue;

            var datePart = item.Time[..spaceIndex];
            var timeRange = item.Time[(spaceIndex + 1)..];
            var tildeIndex = timeRange.IndexOf('~');
            if (tildeIndex < 0) continue;

            if (!TryParse($"{datePart} {timeRange[..tildeIndex]}", out var start) ||
                !TryParse($"{datePart} {timeRange[(tildeIndex + 1)..]}", out var end))
            {
                continue;
            }

            result.Add(new ExamInfo(item.Course, item.Time, item.Room, item.SeatNo, start, end));
        }

        return result;

        static bool TryParse(string value, out DateTime result)
            => DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out result);
    }

    private List<T>? ReadFixture<T>(
        string fileName, JsonTypeInfo<List<T>> typeInfo)
    {
        var path = Path.Combine(_options.FixturePath, fileName);

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("测试数据文件不存在: {Path}", path);
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取测试数据失败: {Path}", path);
            return null;
        }
    }
}

/// <summary>未启用测试账号时的空实现。</summary>
internal sealed class NoOpTestDataProvider : ITestDataProvider
{
    public static readonly NoOpTestDataProvider Instance = new();

    public IReadOnlyList<CourseInfo> GetCourses() => [];

    public IReadOnlyList<ExamInfo> GetExams() => [];

    private NoOpTestDataProvider()
    {
    }
}
