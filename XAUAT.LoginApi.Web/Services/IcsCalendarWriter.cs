using System.Globalization;
using System.Text;

namespace XAUAT.LoginApi.Services;

/// <summary>一个日历事件。</summary>
/// <param name="Uid">全局唯一 ID。</param>
/// <param name="Start">本地时间（Asia/Shanghai），无时区标记。</param>
/// <param name="End">本地时间，同上。</param>
/// <param name="Summary">标题。</param>
/// <param name="Description">描述。</param>
/// <param name="Location">地点。</param>
/// <param name="AlarmDescription">
/// 提醒弹窗的文案。**与 <paramref name="Summary"/> 不同**——Flask 给课程和考试用了两套措辞
/// （「XX考试即将开始！」/「XX课程在XX即将开始！」），照搬以免改变用户看到的提醒内容。
/// </param>
/// <param name="AlarmMinutesBefore">提前多少分钟提醒。</param>
internal sealed record IcsEvent(
    string Uid,
    DateTime Start,
    DateTime End,
    string Summary,
    string Description,
    string Location,
    string AlarmDescription,
    int AlarmMinutesBefore);

/// <summary>
/// ICS（iCalendar）序列化。
/// <para>
/// <b>为什么手写而不引库</b>：Ical.Net 反射很重，在 Native AOT 下会触发 IL2026/IL3050，
/// 而 csproj 把它们设成了编译错误。RFC 5545 的这部分又足够简单，可控性反而更好。
/// </para>
/// <para>
/// 与 Flask（<c>icalendar</c> 库）相比，本实现按计划做了**规范化**：
/// </para>
/// <list type="bullet">
/// <item>补上 <c>VERSION</c>/<c>PRODID</c>/<c>CALSCALE</c>/<c>METHOD</c> 这些必需或约定属性。</item>
/// <item>补上 <c>VTIMEZONE</c>，并给 <c>DTSTART</c>/<c>DTEND</c> 加 <c>TZID=Asia/Shanghai</c>。
/// Flask 只给了一个 <c>X-WR-TIMEZONE</c> 提示，时间实际是"浮动"的（无时区），
/// 依 RFC 5545 属于不合法用法。中国无夏令时，故单个 STANDARD 分量即可。</item>
/// <item>补上 <c>DTSTAMP</c>/<c>SEQUENCE</c>/<c>STATUS</c>。</item>
/// </list>
/// <para>
/// <c>UID</c> 格式与 Flask 保持逐字一致（<c>exam-{课程}-{起始时间}</c> / <c>course-{lessonId}-{起始时间}</c>），
/// 否则已订阅的用户会在日历里看到重复事件。
/// </para>
/// </summary>
internal static class IcsCalendarWriter
{
    /// <summary>时区 ID。与 Flask 的 <c>X-WR-TIMEZONE</c> 取值一致。</summary>
    public const string TimeZoneId = "Asia/Shanghai";

    /// <summary>RFC 5545 规定的内容行最大字节数（不含 CRLF）。</summary>
    private const int MaxLineOctets = 75;

    /// <summary>
    /// 生成完整的 VCALENDAR 文本。返回 UTF-8 字节（无 BOM），行尾为 CRLF。
    /// </summary>
    public static byte[] Create(
        IReadOnlyList<IcsEvent> events, string calendarName, string appleColor, DateTimeOffset generatedAt)
    {
        var builder = new StringBuilder();

        AppendLine(builder, "BEGIN:VCALENDAR");
        AppendLine(builder, "VERSION:2.0");
        AppendLine(builder, "PRODID:-//XAUAT//LoginApi//CN");
        AppendLine(builder, "CALSCALE:GREGORIAN");
        AppendLine(builder, "METHOD:PUBLISH");
        AppendLine(builder, $"X-WR-CALNAME:{EscapeText(calendarName)}");
        AppendLine(builder, $"X-APPLE-CALENDAR-COLOR:{appleColor}");
        AppendLine(builder, $"X-WR-TIMEZONE:{TimeZoneId}");

        AppendTimeZone(builder);

        foreach (var item in events)
        {
            AppendEvent(builder, item, generatedAt);
        }

        AppendLine(builder, "END:VCALENDAR");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// 时区分量。中国自 1991 年起不再使用夏令时，偏移恒为 +08:00，
    /// 因此一个 STANDARD 分量就足以完整描述。
    /// </summary>
    private static void AppendTimeZone(StringBuilder builder)
    {
        AppendLine(builder, "BEGIN:VTIMEZONE");
        AppendLine(builder, $"TZID:{TimeZoneId}");
        AppendLine(builder, "BEGIN:STANDARD");
        AppendLine(builder, "DTSTART:19700101T000000");
        AppendLine(builder, "TZOFFSETFROM:+0800");
        AppendLine(builder, "TZOFFSETTO:+0800");
        AppendLine(builder, "TZNAME:CST");
        AppendLine(builder, "END:STANDARD");
        AppendLine(builder, "END:VTIMEZONE");
    }

    private static void AppendEvent(StringBuilder builder, IcsEvent item, DateTimeOffset generatedAt)
    {
        AppendLine(builder, "BEGIN:VEVENT");
        AppendLine(builder, $"UID:{EscapeText(item.Uid)}");

        // DTSTAMP 必须是 UTC（Z 结尾）
        AppendLine(builder, $"DTSTAMP:{generatedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");

        // 带 TZID 的本地时间：TZID 参数值不加引号（无特殊字符时）
        AppendLine(builder, $"DTSTART;TZID={TimeZoneId}:{item.Start:yyyyMMdd'T'HHmmss}");
        AppendLine(builder, $"DTEND;TZID={TimeZoneId}:{item.End:yyyyMMdd'T'HHmmss}");

        AppendLine(builder, $"SUMMARY:{EscapeText(item.Summary)}");
        AppendLine(builder, $"DESCRIPTION:{EscapeText(item.Description)}");
        AppendLine(builder, $"LOCATION:{EscapeText(item.Location)}");
        AppendLine(builder, "SEQUENCE:0");
        AppendLine(builder, "STATUS:CONFIRMED");
        AppendLine(builder, "TRANSP:OPAQUE");

        AppendLine(builder, "BEGIN:VALARM");
        AppendLine(builder, "ACTION:DISPLAY");
        AppendLine(builder, $"DESCRIPTION:{EscapeText(item.AlarmDescription)}");
        AppendLine(builder, $"TRIGGER:-PT{item.AlarmMinutesBefore}M");
        AppendLine(builder, "END:VALARM");

        AppendLine(builder, "END:VEVENT");
    }

    /// <summary>
    /// 写入一行并做折行。
    /// <para>
    /// RFC 5545 要求内容行不超过 75 个八位组，超出部分折到下一行、以单个空格开头。
    /// <b>按字节而非字符计数</b>：中文一个字 3 字节，「课程表」就已经占 9 字节，
    /// 长教室名很容易超限。折行时不允许把多字节字符切成两半。
    /// </para>
    /// </summary>
    private static void AppendLine(StringBuilder builder, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= MaxLineOctets)
        {
            builder.Append(line).Append("\r\n");
            return;
        }

        var offset = 0;
        var isFirstLine = true;

        while (offset < bytes.Length)
        {
            // 续行开头的那个空格也算在 75 个八位组里
            var limit = isFirstLine ? MaxLineOctets : MaxLineOctets - 1;
            var take = Math.Min(limit, bytes.Length - offset);

            // 若切点正好落在多字节字符中间，回退到该字符的起始字节
            while (take > 0 && offset + take < bytes.Length && (bytes[offset + take] & 0xC0) == 0x80)
            {
                take--;
            }

            // 极端情况（单个字符就超过限长）兜底，避免死循环
            if (take == 0)
            {
                take = Math.Min(limit, bytes.Length - offset);
            }

            if (!isFirstLine)
            {
                builder.Append(' ');
            }

            builder.Append(Encoding.UTF8.GetString(bytes, offset, take)).Append("\r\n");

            offset += take;
            isFirstLine = false;
        }
    }

    /// <summary>
    /// 转义 TEXT 值：反斜杠、分号、逗号、换行。
    /// 注意冒号**不需要**转义（它不是 TEXT 的分隔符）。
    /// </summary>
    private static string EscapeText(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case ';': builder.Append("\\;"); break;
                case ',': builder.Append("\\,"); break;
                case '\r': break;
                case '\n': builder.Append("\\n"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>把 unix 秒格式化成 ICS 的本地时间串（测试与调试用）。</summary>
    internal static string FormatLocal(DateTime value)
        => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
}
