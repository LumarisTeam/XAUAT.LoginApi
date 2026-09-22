namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 一次登录/换票的结果。对应 Flask 的 <c>LoginTokenModel</c>。
/// <para>
/// <see cref="EduCookie"/> 才是能访问教务系统的会话 cookie（<c>__pstsid__</c>/<c>SESSION</c>）；
/// <see cref="SsoCookie"/> 是 CAS 票据（<c>CASTGC</c>），教务系统**不认**它，
/// 必须再经 <c>authserver</c> 换一次票才能拿到 eduCookie。
/// </para>
/// </summary>
internal sealed record LoginToken(bool Success, string Message, string EduCookie, string SsoCookie)
{
    public static LoginToken Failed(string message) => new(false, message, "", "");

    /// <summary>返回给调用方的 cookie 串：优先 eduCookie，退化到 ssoCookie。</summary>
    public string EffectiveCookie => !string.IsNullOrEmpty(EduCookie) ? EduCookie : SsoCookie;
}
