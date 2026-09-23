using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

public class XauatHtmlParserTests
{
    [Fact]
    public void ParseLoginPage_ShouldExtractFieldsRegardlessOfAttributeOrder()
    {
        // BeautifulSoup 是按 name 找元素再读属性，与顺序无关。
        // 用"一条正则匹配整个标签"的实现会把顺序写死，这里专门守住这一点。
        const string html = """
            <form>
              <input value="execution-value" name="execution" type="hidden">
              <input type="hidden" value="lt-value" name="lt">
              <input name="_eventId" type="hidden" value="submit">
            </form>
            """;

        var fields = XauatHtmlParser.ParseLoginPage(html);

        Assert.Equal("lt-value", fields.Lt);
        Assert.Equal("execution-value", fields.Execution);
        Assert.Equal("submit", fields.EventId);
    }

    [Theory]
    [InlineData("""<input name="lt" value="v1">""")]
    [InlineData("""<input name='lt' value='v1'>""")]
    public void ParseLoginPage_ShouldAcceptSingleAndDoubleQuotes(string tag)
    {
        var fields = XauatHtmlParser.ParseLoginPage(tag);

        Assert.Equal("v1", fields.Lt);
    }

    [Fact]
    public void ParseLoginPage_ShouldDefaultEventIdToSubmit()
    {
        // Flask: 找不到 _eventId 时用 'submit'
        var fields = XauatHtmlParser.ParseLoginPage("<form></form>");

        Assert.Equal("submit", fields.EventId);
        Assert.Equal("", fields.Lt);
        Assert.Equal("", fields.Execution);
    }

    [Fact]
    public void ParseLoginPage_ShouldReturnNullSaltWhenInputAbsent()
    {
        // 三态之一：页面上没有 #pwdEncryptSalt -> None -> 走明文密码
        var fields = XauatHtmlParser.ParseLoginPage("<form></form>");

        Assert.Null(fields.EncryptSalt);
    }

    [Fact]
    public void ParseLoginPage_ShouldUseDefaultSaltWhenValueAttributeMissing()
    {
        // 三态之二：有输入框但没写 value -> BeautifulSoup 的 .get('value', DEFAULT)
        var fields = XauatHtmlParser.ParseLoginPage("""<input id="pwdEncryptSalt" type="hidden">""");

        Assert.Equal(XauatConstants.DefaultEncryptSalt, fields.EncryptSalt);
    }

    [Fact]
    public void ParseLoginPage_ShouldReturnEmptySaltWhenValueIsEmpty()
    {
        // 三态之三：有输入框且 value="" -> 拿到空串（**不是**默认盐），下游按明文处理。
        // 这一态最容易被实现成"回退默认盐"，进而发出一份上游解不开的密文。
        var fields = XauatHtmlParser.ParseLoginPage("""<input id="pwdEncryptSalt" value="">""");

        Assert.Equal("", fields.EncryptSalt);
    }

    [Fact]
    public void ParseLoginPage_ShouldReturnSaltValueWhenPresent()
    {
        var fields = XauatHtmlParser.ParseLoginPage("""<input type="hidden" id="pwdEncryptSalt" value="ABCDEFGHIJKLMNOP">""");

        Assert.Equal("ABCDEFGHIJKLMNOP", fields.EncryptSalt);
    }

    [Fact]
    public void ParseMessageSpan_ShouldExtractText()
    {
        const string html = """<div><span id="msg">用户名或密码错误</span></div>""";

        Assert.Equal("用户名或密码错误", XauatHtmlParser.ParseMessageSpan(html));
    }
}
