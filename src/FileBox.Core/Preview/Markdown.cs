using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FileBox.Core.Preview;

/// <summary>
/// プレビュー用の簡易な Markdown → HTML（見出し・段落・箇条書き・番号付き・引用・コード・表・強調・リンク・画像）。
/// 元の HTML はすべてエスケープし、スクリプトは動かさない。リンクは http(s) と mailto だけ有効にする。
/// </summary>
public static partial class Markdown
{
    public static string ToHtml(string markdown, bool dark = false)
    {
        var body = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        string? list = null;   // "ul" / "ol"
        bool quote = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            body.Append("<p>").Append(Inline(string.Join(" ", paragraph))).Append("</p>\n");
            paragraph.Clear();
        }
        void CloseList() { if (list != null) { body.Append($"</{list}>\n"); list = null; } }
        void CloseQuote() { if (quote) { body.Append("</blockquote>\n"); quote = false; } }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph(); CloseList(); CloseQuote();
                string fence = trimmed[..3];
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence); i++) code.Append(lines[i]).Append('\n');
                body.Append("<pre><code>").Append(WebUtility.HtmlEncode(code.ToString())).Append("</code></pre>\n");
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); CloseList(); CloseQuote(); continue; }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph(); CloseList(); CloseQuote();
                int level = heading.Groups[1].Length;
                body.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value.TrimEnd('#', ' '))).Append($"</h{level}>\n");
                continue;
            }
            if (RuleRegex().IsMatch(trimmed)) { FlushParagraph(); CloseList(); CloseQuote(); body.Append("<hr>\n"); continue; }

            // 表: | a | b | の次の行が |---|---|
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                FlushParagraph(); CloseList(); CloseQuote();
                body.Append("<table><thead><tr>");
                foreach (var cell in Cells(trimmed)) body.Append("<th>").Append(Inline(cell)).Append("</th>");
                body.Append("</tr></thead><tbody>\n");
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    body.Append("<tr>");
                    foreach (var cell in Cells(lines[i].Trim())) body.Append("<td>").Append(Inline(cell)).Append("</td>");
                    body.Append("</tr>\n");
                }
                i--;
                body.Append("</tbody></table>\n");
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(); CloseList();
                if (!quote) { body.Append("<blockquote>"); quote = true; }
                body.Append("<p>").Append(Inline(trimmed.TrimStart('>').Trim())).Append("</p>");
                continue;
            }

            var bullet = BulletRegex().Match(line);
            var number = NumberRegex().Match(line);
            if (bullet.Success || number.Success)
            {
                FlushParagraph(); CloseQuote();
                string kind = bullet.Success ? "ul" : "ol";
                if (list != kind) { CloseList(); body.Append($"<{kind}>\n"); list = kind; }
                string text = (bullet.Success ? bullet : number).Groups[1].Value;
                if (text.StartsWith("[ ] ") || text.StartsWith("[x] ") || text.StartsWith("[X] "))
                    text = (text[1] == ' ' ? "☐ " : "☑ ") + text[4..];
                body.Append("<li>").Append(Inline(text)).Append("</li>\n");
                continue;
            }

            CloseList(); CloseQuote();
            paragraph.Add(trimmed);
        }
        FlushParagraph(); CloseList(); CloseQuote();
        return Wrap(body.ToString(), dark);
    }

    static IEnumerable<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim());

    /// <summary>行の中の書式。先にエスケープしてから記号を置き換える。</summary>
    static string Inline(string text)
    {
        // コードの中は他の書式を当てないよう、いったん退避する
        var codes = new List<string>();
        text = CodeRegex().Replace(text, m => { codes.Add(m.Groups[1].Value); return $"\u0001{codes.Count - 1}\u0001"; });
        string s = WebUtility.HtmlEncode(text);
        s = ImageRegex().Replace(s, m => SafeUrl(m.Groups[2].Value) is { } url ? $"<img alt=\"{m.Groups[1].Value}\" src=\"{url}\">" : m.Groups[1].Value);
        s = LinkRegex().Replace(s, m => SafeUrl(m.Groups[2].Value) is { } url ? $"<a href=\"{url}\">{m.Groups[1].Value}</a>" : m.Groups[1].Value);
        s = BoldRegex().Replace(s, "<strong>$2</strong>");
        s = ItalicRegex().Replace(s, "<em>$2</em>");
        s = StrikeRegex().Replace(s, "<del>$1</del>");
        return CodePlaceholderRegex().Replace(s, m => "<code>" + WebUtility.HtmlEncode(codes[int.Parse(m.Groups[1].Value)]) + "</code>");
    }

    /// <summary>リンクは http(s)・mailto だけ（javascript: などは無効）。エスケープ済みの文字列を受け取る。</summary>
    static string? SafeUrl(string url)
    {
        string raw = WebUtility.HtmlDecode(url).Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return WebUtility.HtmlEncode(raw);
        return null;
    }

    static string Wrap(string body, bool dark)
    {
        string fg = dark ? "#e6e6e6" : "#1b1b1b", bg = dark ? "#202020" : "#ffffff", muted = dark ? "#3a3a3a" : "#e5e5e5", code = dark ? "#2b2b2b" : "#f3f3f3";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'">
            <style>
            body{font-family:"Segoe UI","Yu Gothic UI",sans-serif;font-size:14px;line-height:1.6;color:{{fg}};background:{{bg}};margin:16px;}
            h1,h2{border-bottom:1px solid {{muted}};padding-bottom:4px}
            code{font-family:Consolas,monospace;background:{{code}};padding:1px 4px;border-radius:4px}
            pre{background:{{code}};padding:10px;border-radius:6px;overflow:auto}pre code{padding:0;background:none}
            blockquote{margin:0;padding-left:12px;border-left:4px solid {{muted}};opacity:.85}
            table{border-collapse:collapse}th,td{border:1px solid {{muted}};padding:4px 8px}
            img{max-width:100%}a{color:#3e8ede}
            </style></head><body>
            {{body}}
            </body></html>
            """;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")] private static partial Regex TableSeparatorRegex();
    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.*)$")] private static partial Regex NumberRegex();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeRegex();
    [GeneratedRegex("\u0001(\\d+)\u0001")] private static partial Regex CodePlaceholderRegex();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex ImageRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRegex();
    // snake_case の _ や URL の中では強調にしない
    [GeneratedRegex(@"(?<!\w)(\*\*|__)(?!\s)(.+?)(?<!\s)\1(?!\w)")] private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<![\w*])(\*|_)(?![\s*])(.+?)(?<!\s)\1(?!\w)")] private static partial Regex ItalicRegex();
    [GeneratedRegex(@"~~(.+?)~~")] private static partial Regex StrikeRegex();
}
