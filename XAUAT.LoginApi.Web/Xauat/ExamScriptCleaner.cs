using System.Text.RegularExpressions;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 把考试页上的 JS 字面量清洗成合法 JSON。
/// <para>
/// 逐字移植 Flask <c>XAUATParser.extract_exams_from_html</c> 的三步清洗——
/// 顺序不能变，且每一步都对应上游真实的"不合法"之处：
/// </para>
/// <list type="number">
/// <item>单引号换双引号（上游用单引号写字面量）</item>
/// <item><c>undefined</c> → <c>null</c>（JS 允许，JSON 不允许）</item>
/// <item>去掉尾逗号（<c>[1,2,]</c> 在 JS 里合法）</item>
/// </list>
/// <para>
/// 第 3 步在 Python 里是 <c>re.sub(r',(\s*[}\]])', r'\1', s)</c>，注意替换用的是分组
/// <b>整体</b>（含空白），只删掉逗号——这里保持一致，别"顺手"连空白一起吃掉。
/// </para>
/// </summary>
internal static partial class ExamScriptCleaner
{
    [GeneratedRegex(@":\s*undefined\b")]
    private static partial Regex UndefinedRegex();

    [GeneratedRegex(@",(\s*[}\]])")]
    private static partial Regex TrailingCommaRegex();

    public static string ToJson(string jsLiteral)
    {
        var json = jsLiteral.Replace('\'', '"');
        json = UndefinedRegex().Replace(json, ": null");
        json = TrailingCommaRegex().Replace(json, "$1");
        return json;
    }
}
