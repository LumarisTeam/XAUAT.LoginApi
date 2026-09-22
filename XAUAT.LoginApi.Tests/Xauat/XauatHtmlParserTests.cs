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
    public void ParseSemesterId_ShouldMatchSelectedOptionValue()
    {
        // 注意这条正则靠的是 selected="selected" value="..." 里 "selected" value="" 这段子串，
        // 看起来像笔误但确实命中"被选中项"。样本来自真实页面结构。
        const string html = """
            <select id="semesterId">
              <option value="2024-2025-2">2024-2025-2</option>
              <option selected="selected" value="2025-2026-1">2025-2026-1</option>
            </select>
            """;

        Assert.Equal("2025-2026-1", XauatHtmlParser.ParseSemesterId(html));
        Assert.Null(XauatHtmlParser.ParseSemesterId("<select></select>"));
    }

    [Theory]
    // 两个变量名都要认：Flask 用 studentExamInfoVms，EduApi 的 ExamService 用 studentExamList。
    // 真实页面究竟用哪个尚未确认（见 XauatConstants.ExamArrangeUrl 的说明），先两个都支持。
    [InlineData("studentExamInfoVms")]
    [InlineData("studentExamList")]
    public void ParseExamArrayRaw_ShouldExtractJsArrayForBothKnownVariableNames(string variableName)
    {
        // 用拼接而不是插值原始字符串：JS 里的 { } 与 C# 插值的花括号规则会打架
        var html = "<script>\n  var " + variableName + " = [{'a': 1}, {'b': 2}];\n"
                   + "  var other = 1;\n</script>";

        var raw = XauatHtmlParser.ParseExamArrayRaw(html);

        Assert.Equal("[{'a': 1}, {'b': 2}]", raw);
    }

    [Fact]
    public void ParseExamArrayRaw_ShouldReturnNullWhenAbsent()
    {
        Assert.Null(XauatHtmlParser.ParseExamArrayRaw("<html></html>"));
    }

    [Fact]
    public void ParseSemesterIdAndExamArray_ShouldReturnNullOnUnrelatedPage()
    {
        // 真实踩过的坑：/for-std/exam-arrange 少了尾斜杠会返回「学籍信息」页，
        // HTTP 200 且无重定向，解析器必须安静地返回 null 而不是匹配到别的数组。
        const string html = "<html><title>学籍信息</title><script>var data = [1,2];</script></html>";

        Assert.Null(XauatHtmlParser.ParseExamArrayRaw(html));
        Assert.Null(XauatHtmlParser.ParseSemesterId(html));
    }

    [Fact]
    public void ParseMessageSpan_ShouldExtractText()
    {
        const string html = """<div><span id="msg">用户名或密码错误</span></div>""";

        Assert.Equal("用户名或密码错误", XauatHtmlParser.ParseMessageSpan(html));
    }
}
