using System.Text;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

/// <summary>
/// 用**真实抓取并脱敏**的样本驱动解析器。
/// <para>
/// 与同目录下那些手写用例的分工：手写用例守的是"某条分支的逻辑"，
/// 这里守的是"真实上游响应的形状"。凡是形状上的假设（属性顺序、单双引号、
/// 无 <c>name</c> 属性的输入框），只有真实样本说了算——抓取脚本见
/// <c>tools/capture-fixtures.py</c>。
/// </para>
/// <para>
/// fixture 里的文本值已替换为占位符，但**结构逐字保留**。
/// 课表/考试那几份样本随日历功能一起迁走了，现在只剩 CAS 登录页。
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
}
