using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

/// <summary>
/// 用**真实抓取并脱敏**的样本驱动解析器。
/// <para>
/// 与同目录下那些手写用例的分工：手写用例守的是"某条分支的逻辑"，
/// 这里守的是"真实上游响应的形状"。凡是形状上的假设（属性顺序、room 是对象还是字符串、
/// lessonId 是数字还是字符串、时间串格式），只有真实样本说了算——
/// 抓取脚本见 <c>tools/capture-fixtures.py</c>。
/// </para>
/// <para>
/// fixture 里的文本值已替换为占位符，但**结构逐字保留**。
/// </para>
/// </summary>
public class RealFixtureTests
{
    private static string Read(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestFixtures", name), Encoding.UTF8);

    // ================================================================ CAS 登录页

    [Fact]
    public void ParseLoginPage_OnRealCasPage_ShouldExtractExactlyWhatWasCaptured()
    {
        // 这些值就是抓取脚本独立提取出来的那一份（见 capture-report.txt）：
        //   lt = <空>   execution = e1s1…   _eventId = submit   pwdEncryptSalt = AbCdEfGhJkMnPqRs
        var fields = XauatHtmlParser.ParseLoginPage(Read("cas-login-page.html"));

        Assert.Equal("", fields.Lt);
        Assert.Equal("e1s1", fields.Execution);
        Assert.Equal("submit", fields.EventId);
        Assert.Equal("AbCdEfGhJkMnPqRs", fields.EncryptSalt);
    }

    [Fact]
    public void ParseLoginPage_OnRealCasPage_ShouldFindSaltInputThatHasNoNameAttribute()
    {
        // 真实页面上 pwdEncryptSalt 只有 id、没有 name。
        // 这条断言专门守住"先切 <input> 标签、再逐个读属性"的实现方式：
        // 换成"一条正则同时匹配 name 和 value"就会漏掉它，进而把明文密码发出去。
        var html = Read("cas-login-page.html");

        var fields = XauatHtmlParser.ParseLoginPage(html);

        Assert.NotNull(fields.EncryptSalt);
        Assert.Equal(16, Encoding.UTF8.GetByteCount(fields.EncryptSalt!)); // AES-128 密钥长度
        Assert.DoesNotContain("name=\"pwdEncryptSalt\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLoginPage_OnRealCasPage_ShouldNotTreatEmptyLtAsMissing()
    {
        // lt 是 value="" 而不是没有该 input。解析器要原样返回空串。
        var fields = XauatHtmlParser.ParseLoginPage(Read("cas-login-page.html"));

        Assert.Equal(string.Empty, fields.Lt);
        Assert.NotNull(fields.Lt);
    }

    // ================================================================ 学期与课程

    [Fact]
    public void ParseSemesterId_OnRealCourseTablePage_ShouldMatchCapturedValue()
    {
        // 抓取时命中 '361'（2026-2027-1）；下载的样本里选项已脱敏为占位文本，但值保留
        var semester = XauatHtmlParser.ParseSemesterId(Read("course-table.html"));

        Assert.Equal("361", semester);
    }

    [Fact]
    public void ParseSemesterId_ShouldPickTheSelectedOptionNotTheFirstOne()
    {
        // 真实页面的下拉框里被选中项是**最后一个**——靠的是 selected="selected" 那段子串
        var html = Read("course-table.html");

        Assert.Contains("selected=\"selected\" value=\"361\"", html, StringComparison.Ordinal);
        Assert.StartsWith("<option value=", html[html.IndexOf("<option", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void StandardizeCourses_OnRealScheduleDatum_ShouldMapEveryField()
    {
        var courses = StandardizeCoursesFromFixture();

        Assert.Equal(4, courses.Count);

        // 真实抓取的首条：startTime=1010 -> 10:10，endTime=1200 -> 12:00
        var first = courses[0];
        Assert.Equal("220309", first.LessonId);
        Assert.Equal("测试课程甲", first.CourseName);
        Assert.Equal("测试教师甲", first.PersonName);
        Assert.Equal("测试教室甲", first.RoomZh);
        Assert.Equal("2026-09-02", first.Date);
        Assert.Equal(1010, first.StartTime);
        Assert.Equal(new DateTime(2026, 9, 2, 10, 10, 0), first.Start);
        Assert.Equal(new DateTime(2026, 9, 2, 12, 0, 0), first.End);
    }

    [Fact]
    public void StandardizeCourses_ShouldResolveLessonIdToCourseNameAcrossDistinctLessons()
    {
        // 真实响应里 lessonList[].id 与 scheduleList[].lessonId 都是 JSON 数字；
        // 两者必须能配上，否则每门课都会变成 "Unknown Course"
        var courses = StandardizeCoursesFromFixture();

        Assert.Equal(0, courses.Count(c => c.CourseName == "Unknown Course"
                                           && c.LessonId != "999999"));
        Assert.Equal("测试课程乙", courses.Single(c => c.LessonId == "220310").CourseName);
        Assert.Equal("测试课程丙", courses.Single(c => c.LessonId == "220311").CourseName);
    }

    [Fact]
    public void StandardizeCourses_ShouldFallBackToUnknownCourseForUnmappedLessonId()
    {
        // Flask: course_dict.get(schedule['lessonId'], "Unknown Course")
        var courses = StandardizeCoursesFromFixture();

        Assert.Equal("Unknown Course", courses.Single(c => c.LessonId == "999999").CourseName);
    }

    [Fact]
    public void StandardizeCourses_ShouldReadRoomFromObjectAndFromPlainString()
    {
        // 真实抓取里 room 315/315 都是对象（取 nameZh）；字符串分支是 Flask 也处理的情况，
        // 样本里合成了一条，防止有人把字符串分支删掉
        var courses = StandardizeCoursesFromFixture();

        Assert.Equal("测试教室甲", courses.Single(c => c.LessonId == "220309").RoomZh);
        Assert.Equal("测试教室丙", courses.Single(c => c.LessonId == "220311").RoomZh);
    }

    [Fact]
    public void StandardizeCourses_ShouldParseHhmmTimesWithoutLosingLeadingZeros()
    {
        // 800 -> 08:00 而不是 80:00（divmod(800,100) = (8, 0)）
        var courses = StandardizeCoursesFromFixture();
        var second = courses.Single(c => c.LessonId == "220310");

        Assert.Equal(800, second.StartTime);
        Assert.Equal(new DateTime(2026, 9, 2, 8, 0, 0), second.Start);
        Assert.Equal(new DateTime(2026, 9, 2, 9, 50, 0), second.End);
    }

    [Fact]
    public void StandardizeCourses_ShouldNotFailWhenRoomIsNull()
    {
        // 防御：room 为 null 时退到"未知地点"，而不是抛异常把整张课表带崩
        using var document = JsonDocument.Parse("""
            {"lessonList":[{"id":1,"courseName":"A"}],
             "scheduleList":[{"lessonId":1,"personName":"T","room":null,
                              "date":"2026-09-02","startTime":800,"endTime":950}]}
            """);

        var courses = XauatScheduleParser.StandardizeCourses(document, NullLogger.Instance);

        Assert.Single(courses);
        Assert.Equal("未知地点", courses[0].RoomZh);
    }

    [Fact]
    public void StandardizeCourses_ShouldReturnEmptyForNullOrNonObjectInput()
    {
        Assert.Empty(XauatScheduleParser.StandardizeCourses(null, NullLogger.Instance));

        using var document = JsonDocument.Parse("[]");
        Assert.Empty(XauatScheduleParser.StandardizeCourses(document, NullLogger.Instance));
    }

    // ================================================================ 考试页

    [Fact]
    public void ParseExamArrayRaw_OnWrongPageSample_ShouldReturnNull()
    {
        // 真实踩过的坑：/for-std/exam-arrange 少了尾斜杠会返回「学籍信息」页，
        // HTTP 200 且无重定向。那样必须返回 null（进而返回空考试列表），
        // 而不是误匹配到页面上的其它数组（样本里放了 data 和 semesterPattern 两个诱饵）。
        Assert.Null(XauatHtmlParser.ParseExamArrayRaw(Read("exam-arrange-wrong-page.html")));
    }

    [Theory]
    [InlineData("studentExamInfoVms")]
    [InlineData("studentExamList")]
    public void ParseExamArrayRaw_ShouldAcceptBothKnownVariableNames(string variableName)
    {
        var html = "<script>var " + variableName + " = [{\"course\": 1}];</script>";

        Assert.Equal("[{\"course\": 1}]", XauatHtmlParser.ParseExamArrayRaw(html));
    }

    // ================================================================ 辅助

    /// <summary>复刻客户端取 result 再标准化的那一步。</summary>
    private static List<CourseInfo> StandardizeCoursesFromFixture()
    {
        using var document = JsonDocument.Parse(Read("schedule-table-datum.json"));
        using var result = JsonDocument.Parse(
            document.RootElement.GetProperty("result").GetRawText());

        return XauatScheduleParser.StandardizeCourses(result, NullLogger.Instance);
    }
}
