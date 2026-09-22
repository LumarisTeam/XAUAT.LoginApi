using System.Text;
using System.Text.RegularExpressions;
using XAUAT.LoginApi.Services;

namespace XAUAT.LoginApi.Tests.Services;

public class IcsCalendarWriterTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static IcsEvent SampleEvent(
        string summary = "数据结构", string location = "教学楼A101", string alarm = "数据结构课程在教学楼A101即将开始！")
        => new(
            Uid: "course-lesson-1-2026-03-02T08:00:00",
            Start: new DateTime(2026, 3, 2, 8, 0, 0),
            End: new DateTime(2026, 3, 2, 9, 50, 0),
            Summary: summary,
            Description: "张老师",
            Location: location,
            AlarmDescription: alarm,
            AlarmMinutesBefore: 15);

    private static string Create(params IcsEvent[] events)
        => Encoding.UTF8.GetString(IcsCalendarWriter.Create(events, "课程表", "#540EB9", GeneratedAt));

    [Fact]
    public void Create_ShouldEmitCalendarLevelPropertiesMatchingFlask()
    {
        var ics = Create();

        Assert.Contains("BEGIN:VCALENDAR", ics, StringComparison.Ordinal);
        Assert.Contains("X-WR-CALNAME:课程表", ics, StringComparison.Ordinal);
        Assert.Contains("X-APPLE-CALENDAR-COLOR:#540EB9", ics, StringComparison.Ordinal);
        Assert.Contains("X-WR-TIMEZONE:Asia/Shanghai", ics, StringComparison.Ordinal);
        Assert.Contains("END:VCALENDAR", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldIncludeVTimeZoneAndTzidOnDateProperties()
    {
        // 这是相对 Flask 的**有意规范化**：Flask 只给 X-WR-TIMEZONE 提示，
        // 时间实际是"浮动"的（RFC 5545 不允许）。这里补上完整的时区分量。
        var ics = Create(SampleEvent());

        Assert.Contains("BEGIN:VTIMEZONE", ics, StringComparison.Ordinal);
        Assert.Contains("TZID:Asia/Shanghai", ics, StringComparison.Ordinal);
        Assert.Contains("TZOFFSETTO:+0800", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Asia/Shanghai:20260302T080000", ics, StringComparison.Ordinal);
        Assert.Contains("DTEND;TZID=Asia/Shanghai:20260302T095000", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldKeepUidFormatIdenticalToFlask()
    {
        // UID 变了的话，已订阅的用户会在日历里看到重复事件（旧 UID 不会自动消失）
        var ics = Create(SampleEvent());

        Assert.Contains("UID:course-lesson-1-2026-03-02T08:00:00", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldStampDtStampInUtc()
    {
        var ics = Create(SampleEvent());

        Assert.Contains("DTSTAMP:20260922T100000Z", ics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "TRIGGER:-PT30M")]
    [InlineData(15, "TRIGGER:-PT15M")]
    public void Create_ShouldEmitAlarmTrigger(int minutes, string expected)
    {
        var item = SampleEvent() with { AlarmMinutesBefore = minutes };

        Assert.Contains(expected, Create(item), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldUseAlarmDescriptionSeparateFromSummary()
    {
        // Flask 给课程和考试用了两套提醒措辞，不能拿 SUMMARY 顶替
        var ics = Create(SampleEvent(summary: "数据结构", alarm: "数据结构课程在教学楼A101即将开始！"));

        Assert.Contains("SUMMARY:数据结构", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:数据结构课程在教学楼A101即将开始！", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldUseCrlfLineEndings()
    {
        var ics = Create(SampleEvent());

        // 不能有裸露的 \n（即每个 LF 前面都必须是 CR）
        var bareLineFeeds = Regex.Matches(ics, "(?<!\r)\n");
        Assert.Empty(bareLineFeeds);
        Assert.Contains("\r\n", ics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a,b", "a\\,b")]
    [InlineData("a;b", "a\\;b")]
    [InlineData("a\\b", "a\\\\b")]
    public void Create_ShouldEscapeTextValues(string raw, string escaped)
    {
        var ics = Create(SampleEvent(location: raw));

        Assert.Contains($"LOCATION:{escaped}", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldFoldLongLinesAt75OctetsAndUnfoldBackToOriginal()
    {
        // 中文一个字 3 字节，长教室名很容易越过 75 字节那道线；
        // 折行还必须按**字节**切且不能把多字节字符切两半。
        var location = string.Concat(Enumerable.Repeat("很长的教学楼名称", 12));
        var ics = Create(SampleEvent(location: location));

        foreach (var line in ics.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75,
                $"行超过 75 字节：{line}");
        }

        // 展开（去掉 CRLF + 单个空格）后应还原出原始值
        var unfolded = Unfold(ics);
        Assert.Contains($"LOCATION:{location}", unfolded, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShouldNotSplitMultiByteCharactersWhenFolding()
    {
        var location = string.Concat(Enumerable.Repeat("西安建筑科技大学", 15));
        var ics = Create(SampleEvent(location: location));

        // 若能正常 UTF-8 解码且内容完整，说明没有在字符中间切断
        var unfolded = Unfold(ics);
        Assert.Contains(location, unfolded, StringComparison.Ordinal);

        // 反向验证：折行后的每一行都必须是合法 UTF-8（用严格解码器）
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var line in ics.Split("\r\n"))
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            strict.GetString(bytes);
        }
    }

    [Fact]
    public void Create_ShouldEmitEventsInGivenOrder()
    {
        // 调用方（CalendarService）负责"考试在前、课程在后"的顺序，writer 只按传入顺序输出
        var exams = SampleEvent(summary: "考试") with { Uid = "exam-1" };
        var courses = SampleEvent(summary: "课程") with { Uid = "course-1" };

        var ics = Create(exams, courses);

        Assert.True(ics.IndexOf("UID:exam-1", StringComparison.Ordinal)
                    < ics.IndexOf("UID:course-1", StringComparison.Ordinal));
    }

    /// <summary>按 RFC 5545 展开折行：CRLF + 单个空格表示续行。</summary>
    private static string Unfold(string ics)
        => ics.Replace("\r\n ", "", StringComparison.Ordinal);
}
