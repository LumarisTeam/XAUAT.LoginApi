namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 标准化后的一节课。对应 Flask <c>standardize_courses</c> 产出的 dict。
/// <para>
/// 注意这里**没有周次概念**：教务接口直接给出具体日期（<c>date</c>）与 <c>HHMM</c> 形式的上课时间，
/// 所以日历生成完全不需要"第几周 → 哪一天"的换算。这与 EduApi 那套
/// <c>WeekIndexes/Weekday/StartUnit</c> 模型是两条不同的路子，别混。
/// </para>
/// </summary>
/// <param name="LessonId">课程 ID，参与 ICS 的 UID 生成。</param>
/// <param name="CourseName">课程名，映射为 ICS SUMMARY。</param>
/// <param name="PersonName">教师名，映射为 ICS DESCRIPTION。</param>
/// <param name="RoomZh">教室，映射为 ICS LOCATION。</param>
/// <param name="Date">原始日期串 <c>yyyy-MM-dd</c>。</param>
/// <param name="StartTime">原始上课时间，<c>HHMM</c> 整数（如 <c>800</c> 表示 08:00）。</param>
/// <param name="EndTime">原始下课时间，同上。</param>
/// <param name="Start">解析后的本地（Asia/Shanghai）起始时间，无时区。</param>
/// <param name="End">解析后的本地结束时间，无时区。</param>
internal sealed record CourseInfo(
    string LessonId,
    string CourseName,
    string PersonName,
    string RoomZh,
    string Date,
    int StartTime,
    int EndTime,
    DateTime Start,
    DateTime End);

/// <summary>标准化后的一场考试。对应 Flask <c>standardize_exams</c> 产出的 dict。</summary>
/// <param name="Course">课程名，映射为 ICS SUMMARY（拼上"考试"）。</param>
/// <param name="Time">原始时间串，如 <c>2026-06-20 08:00~10:00</c>。</param>
/// <param name="Room">考场。</param>
/// <param name="SeatNo">座位号。</param>
/// <param name="Start">解析后的本地起始时间，无时区。</param>
/// <param name="End">解析后的本地结束时间，无时区。</param>
internal sealed record ExamInfo(
    string Course,
    string Time,
    string Room,
    string SeatNo,
    DateTime Start,
    DateTime End);
