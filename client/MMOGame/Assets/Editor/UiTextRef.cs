using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// D9 之后：屏上的中文不再写在 .cs 里，正本在 config/ui_text/<组>.json，源码只留
// `UiStrings.<组>.<key>` 这样的常量引用。
//
// 编辑器检查原来断言「屏源码里有这句中文」的，改成本类：
//   UiTextRef.In(root, screenSource, "UiBagScreen", "背包")   // 表里有 + 源码引用了它
// 口径没放松（还是「这句真的在屏上」），只是多跳一层。组名 = 源文件 basename。
public static class UiTextRef
{
    // entries 里每条都是 { "key": "T001", "text": "..." }，顺序由生成器保证。
    static readonly Regex Entry = new Regex(
        "\"key\"\\s*:\\s*\"(T\\d+)\"\\s*,\\s*\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    /// <summary>文案表里 group 组下 text 的常量引用（如 `UiStrings.UiBagScreen.T001`）；没有返回 null。</summary>
    public static string Ref(string root, string group, string text)
    {
        var path = Path.Combine(root, "config/ui_text", group + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var body = File.ReadAllText(path);
        foreach (Match m in Entry.Matches(body))
        {
            if (Unescape(m.Groups[2].Value) == text)
            {
                return "UiStrings." + group + "." + m.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>断言「表里有这句，且 source（屏源码）引用了它的常量」。</summary>
    public static bool In(string root, string source, string group, string text)
    {
        var r = Ref(root, group, text);
        return r != null && source.Contains(r);
    }

    static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(s[i]); break;
                }
                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}
