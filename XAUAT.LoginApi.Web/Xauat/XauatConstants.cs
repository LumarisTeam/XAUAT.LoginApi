namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// XAUAT 统一认证的协议常量。
/// 全部照抄 Flask（<c>xauat_sso_login.py</c>），改动前请先确认上游行为。
/// <para>
/// 教务侧的常量（学期、课表、考试）随日历功能一起迁到了 XAUAT.EduApi，见其
/// <c>ExamService</c> / <c>CourseService</c>。
/// </para>
/// </summary>
internal static class XauatConstants
{
    /// <summary>
    /// 统一认证入口。
    /// <b>注意是明文 HTTP</b>——上游如此，不要"顺手"改成 https（证书与端口都未必对得上）。
    /// </summary>
    public const string AuthServerUrl = "http://authserver.xauat.edu.cn/authserver/login";

    /// <summary>认证服务器主机名，用于 cookie 归属。</summary>
    public const string AuthServerHost = "authserver.xauat.edu.cn";

    /// <summary>CAS 登录后要跳转的目标服务（教务门户的 SSO 入口）。</summary>
    public const string ServiceUrl = "https://swjw.xauat.edu.cn/student/sso/login";

    /// <summary>教务系统主机名，用于 cookie 归属。</summary>
    public const string StudentHost = "swjw.xauat.edu.cn";

    /// <summary>
    /// <c>service</c> 查询参数的两种编码形态。两者**不同**，必须分别复刻：
    /// <list type="bullet">
    /// <item>登录页 GET 与登录 POST 用 <c>requests</c> 的 <c>params=</c>，会把 <c>/</c> 编成 <c>%2F</c>。</item>
    /// <item><c>loginFromSSO</c> 手工拼接，用的是 <c>quote()</c>（默认 <c>safe='/'</c>），斜杠保持原样。</item>
    /// </list>
    /// 统一成一种会更"干净"，但那是在改上游协议，不是重构。
    /// </summary>
    public const string ServiceUrlEncoded = "https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin";

    /// <summary><c>loginFromSSO</c> 用的形态：斜杠不编码。</summary>
    public const string ServiceUrlQuoted = "https%3A//swjw.xauat.edu.cn/student/sso/login";

    /// <summary>密码加密用的字符表（49 字符，去掉了易混淆的 I/l/o/0/1/9）。</summary>
    public const string AesChars = "ABCDEFGHJKMNPQRSTWXYZabcdefhijkmnprstwxyz2345678";

    /// <summary>登录页存在 <c>#pwdEncryptSalt</c> 输入框但没有 <c>value</c> 属性时的回退盐值。</summary>
    public const string DefaultEncryptSalt = "rjBFAaHsNkKAhpoi";

    /// <summary>浏览器 UA，与 Flask 完全一致。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/91.0.4472.124 Safari/537.36";

    public const string AcceptHtml =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";

    public const string AcceptLanguage = "zh-CN,zh;q=0.8,en-US;q=0.5,en;q=0.3";

    /// <summary>认证服务器连接的默认超时（Flask 默认 <c>(30, 60)</c>）。</summary>
    public static readonly TimeSpan AuthServerTimeout = TimeSpan.FromSeconds(30);
}
