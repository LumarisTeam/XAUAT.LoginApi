using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using XAUAT.LoginApi.Redis;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// XAUAT 教务系统客户端：当前学期、课程表、考试安排。
/// <para>
/// 移植自 Flask 的 <c>xauat_client.py</c> + <c>xauat_parser.py</c>。
/// 与 Flask 的两点差异（均为简化，不改变可观测行为）：
/// </para>
/// <list type="bullet">
/// <item>Flask 的 <c>fetch_courses()</c> 与 <c>process_course_data()</c> 在失败时会各打一次
/// <c>get-data</c>（第二次是重试）；这里合并为一次，失败即返回空列表，不再重打上游。</item>
/// <item>Flask 用会话对象持有 <c>is_authenticated</c>/<c>courses</c>/<c>exams</c> 等可变状态，
/// 这里改为方法内传参，避免共享状态（也便于并发）。</item>
/// </list>
/// </summary>
internal sealed class XauatAcademicClient(
    IHttpClientFactory httpClientFactory,
    ILoginRedisStore redis,
    ILogger<XauatAcademicClient> logger)
{
    /// <summary>不跟随重定向、不管 cookie 的命名客户端（cookie 由 <see cref="XauatCookieJar"/> 手工管理）。</summary>
    public const string ClientName = "XauatStudentClient";

    /// <summary>
    /// 用教务会话 cookie 建立一个会话。
    /// <para>
    /// 每次调用都要新建：并发登录之间绝不能共享 cookie 罐（否则会串号），
    /// 详见 <see cref="XauatCookieJar"/> 的说明。
    /// </para>
    /// </summary>
    public static XauatCookieJar CreateSession(string eduCookie)
    {
        var jar = new XauatCookieJar();
        jar.Seed(XauatConstants.StudentHost, eduCookie);
        return jar;
    }

    /// <summary>
    /// 当前学期 id（如 <c>2025-2026-1</c>）。优先读 Redis 的全局键 <c>current_semester</c>。
    /// <para>
    /// 该键**全局共享、不按用户区分**（Flask 如此），TTL 7 天。
    /// </para>
    /// </summary>
    public async Task<string?> GetCurrentSemesterAsync(XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var cached = await redis.GetStringAsync(LoginCacheKeys.CurrentSemester);
        if (!string.IsNullOrEmpty(cached))
        {
            logger.LogInformation("从缓存获取当前学期: {Semester}", cached);
            return cached;
        }

        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{XauatConstants.StudentBaseUrl}/for-std/course-table");
            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);

            var html = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);
            var semester = XauatHtmlParser.ParseSemesterId(html);

            if (semester is not null)
            {
                await redis.SetStringAsync(LoginCacheKeys.CurrentSemester, semester, LoginCacheTtl.CurrentSemester);
                logger.LogInformation("从教务系统获取当前学期并缓存: {Semester}", semester);
            }
            else
            {
                logger.LogWarning("课表页未解析出当前学期 id（正则 selected\" value=\" 未命中）");
            }

            return semester;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取当前学期时出错");
            return null;
        }
    }

    /// <summary>
    /// 取整学期的课程明细并标准化。对应 Flask 的 <c>fetch_courses</c> + <c>fetch_course_details</c>
    /// + <c>standardize_courses</c>。
    /// </summary>
    public async Task<List<CourseInfo>> GetScheduleAsync(
        XauatCookieJar jar, string semesterId, CancellationToken cancellationToken)
    {
        var lessonIds = await FetchLessonIdsAsync(jar, semesterId, cancellationToken);
        if (lessonIds.Count == 0)
        {
            logger.LogWarning("课程列表为空，无法获取课程详细信息");
            return [];
        }

        var details = await FetchCourseDetailsAsync(jar, lessonIds, cancellationToken);
        return XauatScheduleParser.StandardizeCourses(details, logger);
    }

    private async Task<List<JsonElement>> FetchLessonIdsAsync(
        XauatCookieJar jar, string semesterId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var url = $"{XauatConstants.StudentBaseUrl}/for-std/course-table/get-data" +
                  $"?bizTypeId=2&semesterId={semesterId}&dataId=";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", XauatConstants.AcceptJson);

            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
            var body = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

            // 会话过期时教务系统会返回 HTML 登录页而不是 JSON——这是最典型的失效信号
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "课程表接口返回非 JSON 内容 ({ContentType})，可能是会话已过期，响应前 200 字符: {Body}",
                    contentType, Truncate(body, 200));
                return [];
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("lessonIds", out var lessonIds) ||
                lessonIds.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("课程表接口响应缺少 lessonIds 字段，实际响应: {Body}", Truncate(body, 500));
                return [];
            }

            var result = lessonIds.EnumerateArray().ToList();
            logger.LogInformation("成功获取课程列表，共 {Count} 门课程", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程列表失败");
            return [];
        }
    }

    private async Task<JsonDocument?> FetchCourseDetailsAsync(
        XauatCookieJar jar, List<JsonElement> lessonIds, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{XauatConstants.StudentBaseUrl}/ws/schedule-table/datum")
            {
                Content = JsonContent.Create(
                    new ScheduleTableRequest { LessonIds = lessonIds },
                    XauatJsonContext.Default.ScheduleTableRequest)
            };
            request.Headers.TryAddWithoutValidation("Accept", XauatConstants.AcceptJson);

            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
            var body = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("课程详细信息响应内容类型异常: {ContentType}", contentType);
                return null;
            }

            var document = JsonDocument.Parse(body);
            logger.LogInformation("成功获取课程详细信息");

            // 只取 result，后续都相对它导航；document 的生命周期由调用方（本方法返回后）持有
            return JsonDocument.Parse(
                document.RootElement.TryGetProperty("result", out var result) ? result.GetRawText() : "null");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程详细信息失败");
            return null;
        }
    }

    /// <summary>
    /// 取考试安排并标准化。对应 Flask 的 <c>fetch_exams</c>（它内部已完成标准化，
    /// 所以 Flask 的 <c>process_exam_data</c> 是个空函数）。
    /// <para>
    /// 注意 Flask 这个函数**没有登录态检查**——会话失效时它同样会去请求，
    /// 拿不到数据就返回空列表。这里保持一致。
    /// </para>
    /// </summary>
    public async Task<List<ExamInfo>> GetExamsAsync(XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, XauatConstants.ExamArrangeUrl);
            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);

            var html = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);
            var raw = XauatHtmlParser.ParseExamArrayRaw(html);
            if (raw is null)
            {
                logger.LogWarning("未找到考试安排信息（studentExamInfoVms 未命中）");
                return [];
            }

            var exams = XauatScheduleParser.StandardizeExams(raw, logger);
            logger.LogInformation("成功获取考试安排，共 {Count} 门考试", exams.Count);
            return exams;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取考试安排失败");
            return [];
        }
    }

    private static List<ExamInfo> StandardizeExams(string rawJsLiteral, ILogger logger)
        => XauatScheduleParser.StandardizeExams(rawJsLiteral, logger);
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
