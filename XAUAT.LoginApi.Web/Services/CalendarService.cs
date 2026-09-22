using System.Globalization;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Services;

/// <summary>日历生成的结果类型，直接决定 HTTP 状态码。</summary>
internal enum CalendarOutcome
{
    /// <summary>200，返回 ICS 正文。</summary>
    Success,

    /// <summary>401：登录失败或拿不到数据。</summary>
    AuthFailed,

    /// <summary>403：账号在封禁中。</summary>
    Banned,

    /// <summary>500：其他错误（含不支持的学校）。</summary>
    Error
}

internal sealed record CalendarResult(CalendarOutcome Outcome, byte[]? Content, string? Error);

/// <summary>
/// 日历生成编排。移植自 Flask 的 <c>CalendarService</c> + <c>CalendarGenerator</c>。
/// <para>
/// 事件顺序与 Flask 一致：<b>先考试、后课程</b>。UID 也保持逐字相同的格式
/// （<c>exam-{课程}-{起始时间}</c> / <c>course-{lessonId}-{起始时间}</c>），
/// 否则已订阅的用户会在日历里看到重复事件。
/// </para>
/// </summary>
internal sealed class CalendarService(
    AuthService authService,
    BanService banService,
    XauatAcademicClient academicClient,
    ITestAccountResolver testAccountResolver,
    ITestDataProvider testDataProvider,
    ILogger<CalendarService> logger)
{
    private const string CalendarName = "课程表";
    private const string AppleColor = "#540EB9";

    private const string BannedError = "账户已被暂时封禁，请稍后重试或联系管理员";
    private const string AuthFailedError = "认证失败或数据获取错误";

    /// <param name="school">学校代码，仅支持 xauat。</param>
    /// <param name="username">学号。</param>
    /// <param name="password">密码。</param>
    /// <param name="filter">仅 <c>future</c> 有效：只保留结束时间在当下之后的事件。</param>
    public async Task<CalendarResult> GenerateAsync(
        string school, string username, string password, string? filter, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating calendar for {School}, user: {Username}", school, username);

        var schoolKey = school.ToLowerInvariant();

        // ---- 封禁检查在一切之前（Flask 同序），且对未知学校也会先走到这里
        if (await banService.IsBannedAsync(schoolKey, username))
        {
            logger.LogWarning("Account banned for calendar request: {School}:{Username}", schoolKey, username);
            return new CalendarResult(CalendarOutcome.Banned, null, BannedError);
        }

        if (schoolKey != "xauat")
        {
            // Flask 的工厂会抛 ValueError，被资源的兜底 except 转成 500 + str(e)
            return new CalendarResult(CalendarOutcome.Error, null, $"Unsupported school: {school}");
        }

        // ---- 测试账号：用固定的课表/考试数据生成日历，完全绕开上游
        if (testAccountResolver.IsTestLogin(username, password))
        {
            logger.LogInformation("用户 {Username} 命中测试账号，使用 TestFixtures 生成日历", username);
            var testEvents = BuildEvents(testDataProvider.GetExams(), testDataProvider.GetCourses(), filter);
            return new CalendarResult(
                CalendarOutcome.Success,
                IcsCalendarWriter.Create(testEvents, CalendarName, AppleColor, DateTimeOffset.UtcNow),
                null);
        }

        try
        {
            // ---- 登录（内部走缓存 → SSO 换票 → 完整登录 三级优先级）
            var auth = await authService.LoginAsync(schoolKey, username, password, cancellationToken);
            if (!auth.Success || string.IsNullOrEmpty(auth.Cookies))
            {
                logger.LogWarning("Authentication failed");
                return new CalendarResult(CalendarOutcome.AuthFailed, null, AuthFailedError);
            }

            var jar = XauatAcademicClient.CreateSession(auth.Cookies);

            // ---- 取数据。注意教务接口直接给具体日期，不需要"第几周 → 哪天"的换算
            var semester = await academicClient.GetCurrentSemesterAsync(jar, cancellationToken);
            if (semester is null)
            {
                // Flask 此处不中断：拿不到学期时课程为空，考试仍会尝试拉取
                logger.LogWarning("登录成功但无法获取当前学期，课程表将为空");
            }

            var courses = semester is null
                ? []
                : await academicClient.GetScheduleAsync(jar, semester, cancellationToken);

            var exams = await academicClient.GetExamsAsync(jar, cancellationToken);

            // ---- 生成 ICS
            var events = BuildEvents(exams, courses, filter);
            var content = IcsCalendarWriter.Create(events, CalendarName, AppleColor, DateTimeOffset.UtcNow);

            logger.LogInformation("Calendar generation complete: {ExamCount} 场考试, {CourseCount} 节课",
                exams.Count, courses.Count);

            return new CalendarResult(CalendarOutcome.Success, content, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating calendar");
            return new CalendarResult(CalendarOutcome.Error, null, ex.Message);
        }
    }

    /// <summary>
    /// 组装事件列表。考试在前、课程在后（与 Flask 的产出顺序一致）。
    /// </summary>
    private static List<IcsEvent> BuildEvents(
        IReadOnlyList<ExamInfo> exams, IReadOnlyList<CourseInfo> courses, string? filter)
    {
        var onlyFuture = string.Equals(filter, "future", StringComparison.Ordinal);
        var events = new List<IcsEvent>(exams.Count + courses.Count);

        foreach (var exam in exams)
        {
            if (onlyFuture && exam.End <= DateTime.Now) continue;

            events.Add(new IcsEvent(
                Uid: $"exam-{exam.Course}-{IsoFormat(exam.Start)}",
                Start: exam.Start,
                End: exam.End,
                Summary: $"{exam.Course}考试",
                Description: $"考试时间: {exam.Time}",
                Location: $"教室: {exam.Room} 座位号: {exam.SeatNo}",
                AlarmDescription: $"{exam.Course}考试即将开始！",
                AlarmMinutesBefore: 30));
        }

        foreach (var course in courses)
        {
            if (onlyFuture && course.End <= DateTime.Now) continue;

            events.Add(new IcsEvent(
                Uid: $"course-{course.LessonId}-{IsoFormat(course.Start)}",
                Start: course.Start,
                End: course.End,
                Summary: course.CourseName,
                Description: course.PersonName,
                Location: course.RoomZh,
                AlarmDescription: $"{course.CourseName}课程在{course.RoomZh}即将开始！",
                AlarmMinutesBefore: 15));
        }

        return events;
    }

    /// <summary>
    /// 复刻 Python 的 <c>datetime.isoformat()</c>：<c>2026-06-20T08:00:00</c>。
    /// 秒数为 0 时 Python **仍会写出来**，微秒为 0 时省略——这里保持一致，
    /// 否则 UID 会变，老订阅者会看到重复事件。
    /// </summary>
    private static string IsoFormat(DateTime value)
        => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
