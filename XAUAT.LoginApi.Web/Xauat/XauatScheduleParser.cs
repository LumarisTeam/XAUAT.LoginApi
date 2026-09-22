using System.Globalization;
using System.Text.Json;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 教务原始数据 → 标准模型的转换。
/// <para>
/// 从 <see cref="XauatAcademicClient"/> 里抽出来，是为了能脱离 HTTP 单独验证：
/// 上游响应形状（<c>room</c> 是对象还是字符串、<c>lessonId</c> 的类型、时间串的格式）
/// 只有真实样本说了算，而这些转换正是最容易写错的地方。
/// 抓到的样本见 <c>XAUAT.LoginApi.Tests/TestFixtures/</c>。
/// </para>
/// <para>
/// 逻辑逐行对齐 Flask 的 <c>XAUATParser.standardize_courses</c> / <c>standardize_exams</c>，
/// 包括它"单条坏数据只跳过该条、不影响整体"的容错策略。
/// </para>
/// </summary>
internal static class XauatScheduleParser
{
    // ---------------------------------------------------------------- 标准化

    public static List<ExamInfo> StandardizeExams(string rawJsLiteral, ILogger logger)
    {
        var json = ExamScriptCleaner.ToJson(rawJsLiteral);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Flask: except json.JSONDecodeError -> log -> return []
            logger.LogError(ex, "考试数据 JSON 解析失败");
            return [];
        }

        var result = new List<ExamInfo>();
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var exam in document.RootElement.EnumerateArray())
            {
                try
                {
                    // Flask 用 exam['course']['nameZh'] 这种硬取，缺键即 KeyError → 跳过本条
                    if (!exam.TryGetProperty("course", out var course) ||
                        !course.TryGetProperty("nameZh", out var nameZh))
                    {
                        throw new KeyNotFoundException("course.nameZh");
                    }

                    if (!exam.TryGetProperty("examGroup", out var examGroup) ||
                        !examGroup.TryGetProperty("examTime", out var examTime) ||
                        !examTime.TryGetProperty("dateTimeString", out var dateTimeString))
                    {
                        throw new KeyNotFoundException("examGroup.examTime.dateTimeString");
                    }

                    var time = dateTimeString.GetString() ?? "";

                    // 考场可能缺失，也可能 room 为 null
                    var room = "未知地点";
                    if (exam.TryGetProperty("examPlace", out var examPlace))
                    {
                        if (examPlace.TryGetProperty("room", out var roomElement) &&
                            roomElement.ValueKind == JsonValueKind.Object &&
                            roomElement.TryGetProperty("nameZh", out var roomName) &&
                            roomName.ValueKind == JsonValueKind.String)
                        {
                            room = roomName.GetString() ?? "未知地点";
                        }
                    }
                    else
                    {
                        throw new KeyNotFoundException("examPlace");
                    }

                    if (!exam.TryGetProperty("seatNo", out var seatElement))
                    {
                        throw new KeyNotFoundException("seatNo");
                    }

                    var seat = seatElement.ValueKind == JsonValueKind.String &&
                               !string.IsNullOrEmpty(seatElement.GetString())
                        ? seatElement.GetString()!
                        : "未知座位号";

                    // "2026-06-20 08:00~10:00" -> date "2026-06-20"，timeRange "08:00~10:00"
                    var spaceIndex = time.IndexOf(' ');
                    if (spaceIndex < 0) throw new FormatException($"考试时间格式异常: {time}");

                    var datePart = time[..spaceIndex];
                    var timeRange = time[(spaceIndex + 1)..];
                    var tildeIndex = timeRange.IndexOf('~');
                    if (tildeIndex < 0) throw new FormatException($"考试时间区间格式异常: {time}");

                    var start = ParseLocalDateTime($"{datePart} {timeRange[..tildeIndex]}");
                    var end = ParseLocalDateTime($"{datePart} {timeRange[(tildeIndex + 1)..]}");

                    result.Add(new ExamInfo(nameZh.GetString() ?? "", time, room, seat, start, end));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "处理考试条目失败，已跳过");
                }
            }
        }

        return result;
    }

    public static List<CourseInfo> StandardizeCourses(JsonDocument? details, ILogger logger)
    {
        if (details is null) return [];

        var result = new List<CourseInfo>();
        using (details)
        {
            if (details.RootElement.ValueKind != JsonValueKind.Object) return result;

            // lessonId -> courseName 映射
            var courseNames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (details.RootElement.TryGetProperty("lessonList", out var lessonList) &&
                lessonList.ValueKind == JsonValueKind.Array)
            {
                foreach (var lesson in lessonList.EnumerateArray())
                {
                    if (!lesson.TryGetProperty("id", out var id)) continue;
                    var key = CanonicalId(id);
                    if (key is null) continue;

                    var name = lesson.TryGetProperty("courseName", out var courseName) &&
                               courseName.ValueKind == JsonValueKind.String
                        ? courseName.GetString() ?? ""
                        : "";
                    courseNames[key] = name;
                }
            }

            if (!details.RootElement.TryGetProperty("scheduleList", out var scheduleList) ||
                scheduleList.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var schedule in scheduleList.EnumerateArray())
            {
                try
                {
                    if (!schedule.TryGetProperty("lessonId", out var lessonIdElement))
                    {
                        throw new KeyNotFoundException("lessonId");
                    }

                    var lessonId = CanonicalId(lessonIdElement) ?? "";
                    var courseName = courseNames.GetValueOrDefault(lessonId, "Unknown Course");

                    var personName = schedule.TryGetProperty("personName", out var person) &&
                                     person.ValueKind == JsonValueKind.String
                        ? person.GetString() ?? "Unknown Teacher"
                        : "Unknown Teacher";

                    if (!schedule.TryGetProperty("date", out var dateElement) ||
                        dateElement.ValueKind != JsonValueKind.String)
                    {
                        throw new KeyNotFoundException("date");
                    }

                    var date = dateElement.GetString()!;

                    if (!schedule.TryGetProperty("startTime", out var startElement) ||
                        !schedule.TryGetProperty("endTime", out var endElement))
                    {
                        throw new KeyNotFoundException("startTime/endTime");
                    }

                    var startTime = ReadHhmm(startElement);
                    var endTime = ReadHhmm(endElement);

                    // 日期 "2026-03-02" 与 HHMM 组合成具体时刻
                    var dateParts = date.Split('-');
                    if (dateParts.Length != 3) throw new FormatException($"课程日期格式异常: {date}");

                    var year = int.Parse(dateParts[0], CultureInfo.InvariantCulture);
                    var month = int.Parse(dateParts[1], CultureInfo.InvariantCulture);
                    var day = int.Parse(dateParts[2], CultureInfo.InvariantCulture);

                    var start = new DateTime(year, month, day, startTime / 100, startTime % 100, 0, DateTimeKind.Unspecified);
                    var end = new DateTime(year, month, day, endTime / 100, endTime % 100, 0, DateTimeKind.Unspecified);

                    result.Add(new CourseInfo(
                        lessonId, courseName, personName, ReadRoom(schedule), date, startTime, endTime, start, end));
                }
                catch (Exception ex)
                {
                    // Flask 这里是最宽的 except Exception —— 单条坏数据不影响整张课表
                    logger.LogWarning(ex, "处理课程条目失败，已跳过");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 教室字段可能是对象（取 <c>nameZh</c>）也可能是字符串。Flask 两者都处理，缺省 <c>未知地点</c>。
    /// </summary>
    private static string ReadRoom(JsonElement schedule)
    {
        if (!schedule.TryGetProperty("room", out var room)) return "未知地点";

        return room.ValueKind switch
        {
            JsonValueKind.Object when room.TryGetProperty("nameZh", out var name) &&
                                      name.ValueKind == JsonValueKind.String
                => name.GetString() ?? "未知地点",
            JsonValueKind.String => room.GetString() ?? "未知地点",
            _ => "未知地点"
        };
    }

    /// <summary>
    /// 读取 <c>HHMM</c> 形式的时间。数字是上游的实际形态；
    /// 这里额外接受数字字符串（Flask 遇到字符串会 <c>TypeError</c> → 跳过该条课），
    /// 属于"只会把被丢掉的课捡回来"的宽松处理，正常数据下与 Flask 完全一致。
    /// </summary>
    private static int ReadHhmm(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.GetInt32();

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"时间字段类型异常: {element.ValueKind}");
    }

    /// <summary>
    /// 把 id 归一成字符串键。
    /// <para>
    /// Python 用原生类型做 dict 键，数字与字符串不互通；这里统一成字符串，
    /// 因此上游若在两处用了不同类型，我们仍能匹配上（Flask 会取到 "Unknown Course"）。
    /// 属于严格更优的行为。
    /// </para>
    /// </summary>
    private static string? CanonicalId(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.String => element.GetString(),
        _ => null
    };

    private static DateTime ParseLocalDateTime(string value)
        => DateTime.ParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None);
}
