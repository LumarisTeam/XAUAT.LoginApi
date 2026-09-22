namespace XAUAT.LoginApi.Configuration;

/// <summary>
/// 测试账号配置。
/// 与 PaymentAPI/EduApi 同名同形，便于用同一套 .env 变量在三者间切来切去。
/// </summary>
public class TestAccountOptions
{
    public const string SectionName = "TestAccount";

    public bool Enabled { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string CookieMarker { get; set; } = "";
    public string FixturePath { get; set; } = "TestFixtures";
}
