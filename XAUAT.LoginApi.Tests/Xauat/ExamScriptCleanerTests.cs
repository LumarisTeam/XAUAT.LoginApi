using System.Text.Json;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

public class ExamScriptCleanerTests
{
    [Fact]
    public void ToJson_ShouldReplaceSingleQuotesWithDoubleQuotes()
    {
        Assert.Equal("""{"a": 1}""", ExamScriptCleaner.ToJson("{'a': 1}"));
    }

    [Fact]
    public void ToJson_ShouldReplaceUndefinedWithNull()
    {
        Assert.Equal("""{"a": null}""", ExamScriptCleaner.ToJson("{'a': undefined}"));
    }

    [Fact]
    public void ToJson_ShouldStripTrailingCommas()
    {
        Assert.Equal("[1, 2]", ExamScriptCleaner.ToJson("[1, 2,]"));
        Assert.Equal("""{"a": 1}""", ExamScriptCleaner.ToJson("{'a': 1,}"));
    }

    [Fact]
    public void ToJson_ShouldKeepWhitespaceWhenStrippingTrailingComma()
    {
        // Flask 的正则是 re.sub(r',(\s*[}\]])', r'\1', s)：替换的是"逗号+空白+右括号"整体，
        // 但替换内容里保留了空白，因此只删逗号。若把空白一起吃掉，输出虽仍是合法 JSON，
        // 却和 Flask 的产物对不上了。这里用拼接构造换行，避免依赖 C# 转义写法。
        var newline = "\n";
        Assert.Equal("[1" + newline + " ]", ExamScriptCleaner.ToJson("[1," + newline + " ]"));
    }

    [Fact]
    public void ToJson_ShouldProduceParseableJsonForRealisticPayload()
    {
        const string jsLiteral = "[{'seatNo': '12', 'room': undefined, 'name': 'A',},]";

        var json = ExamScriptCleaner.ToJson(jsLiteral);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }
}
