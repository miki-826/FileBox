namespace FileBox.Core.Preview;

public enum TokenKind { Text, Keyword, String, Comment, Number }

/// <summary>色分けの 1 区切り。</summary>
public readonly record struct Token(TokenKind Kind, string Text);

/// <summary>
/// プレビュー用の軽い色分け（02_機能設計 §3.6）。構文解析はせず、キーワード・文字列・コメント・数値だけを見分ける。
/// 長いファイルは呼び出し側が先頭だけを渡す。
/// </summary>
public static class SyntaxHighlighter
{
    sealed record Language(string[] LineComments, (string Open, string Close)[] BlockComments, char[] Quotes, HashSet<string> Keywords);

    static HashSet<string> Words(string s, bool ignoreCase = false) =>
        new(s.Split(' ', StringSplitOptions.RemoveEmptyEntries), ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    static readonly Language CLike = new(new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'', '`' }, Words(
        "abstract as async await base bool break byte case catch char class const continue default delegate do double else enum event explicit extern " +
        "false finally fixed float for foreach function goto if implicit import in int interface internal is let lock long namespace new null object " +
        "operator out override package params private protected public readonly record ref return sealed short static string struct switch this throw " +
        "true try typeof uint ulong using var virtual void volatile while yield export from extends implements type undefined val fun def nil"));
    static readonly Language Python = new(new[] { "#" }, Array.Empty<(string, string)>(), new[] { '"', '\'' }, Words(
        "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self"));
    static readonly Language Shell = new(new[] { "#" }, Array.Empty<(string, string)>(), new[] { '"', '\'' }, Words(
        "if then else elif fi for in do done while case esac function return echo export local set unset param begin process end foreach switch try catch finally", ignoreCase: true));
    static readonly Language Sql = new(new[] { "--" }, new[] { ("/*", "*/") }, new[] { '\'', '"' }, Words(
        "select from where and or not insert into values update set delete create table index view drop alter join left right inner outer on group by order having limit as distinct null is in like between case when then else end primary key foreign references", ignoreCase: true));
    static readonly Language Markup = new(Array.Empty<string>(), new[] { ("<!--", "-->") }, new[] { '"', '\'' }, new HashSet<string>());
    static readonly Language Ini = new(new[] { ";", "#" }, Array.Empty<(string, string)>(), new[] { '"' }, new HashSet<string>());

    static readonly Dictionary<string, Language> ByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = CLike, [".java"] = CLike, [".js"] = CLike, [".mjs"] = CLike, [".ts"] = CLike, [".tsx"] = CLike, [".jsx"] = CLike,
        [".c"] = CLike, [".h"] = CLike, [".cpp"] = CLike, [".hpp"] = CLike, [".go"] = CLike, [".rs"] = CLike, [".kt"] = CLike,
        [".swift"] = CLike, [".dart"] = CLike, [".json"] = CLike, [".css"] = CLike, [".scss"] = CLike, [".php"] = CLike,
        [".py"] = Python, [".rb"] = Python, [".r"] = Python, [".yaml"] = Python, [".yml"] = Python, [".toml"] = Python,
        [".ps1"] = Shell, [".psm1"] = Shell, [".sh"] = Shell, [".bash"] = Shell, [".bat"] = Shell, [".cmd"] = Shell,
        [".sql"] = Sql,
        [".xml"] = Markup, [".xaml"] = Markup, [".html"] = Markup, [".htm"] = Markup, [".csproj"] = Markup, [".svg"] = Markup, [".config"] = Markup,
        [".ini"] = Ini, [".cfg"] = Ini, [".conf"] = Ini, [".properties"] = Ini, [".editorconfig"] = Ini,
    };

    /// <summary>この拡張子を色分けできるか。</summary>
    public static bool Supports(string ext) => ByExt.ContainsKey(ext);

    public static IReadOnlyList<Token> Tokenize(string text, string ext)
    {
        var tokens = new List<Token>();
        if (!ByExt.TryGetValue(ext, out var lang)) { tokens.Add(new(TokenKind.Text, text)); return tokens; }
        var plain = new System.Text.StringBuilder();
        void Flush() { if (plain.Length > 0) { tokens.Add(new(TokenKind.Text, plain.ToString())); plain.Clear(); } }
        void Emit(TokenKind kind, int start, int end) { Flush(); tokens.Add(new(kind, text[start..end])); }

        int i = 0;
        while (i < text.Length)
        {
            // コメント
            string? line = lang.LineComments.FirstOrDefault(c => string.CompareOrdinal(text, i, c, 0, c.Length) == 0);
            if (line != null)
            {
                int end = text.IndexOf('\n', i);
                Emit(TokenKind.Comment, i, end < 0 ? text.Length : end);
                i = end < 0 ? text.Length : end;
                continue;
            }
            var block = lang.BlockComments.FirstOrDefault(b => string.CompareOrdinal(text, i, b.Open, 0, b.Open.Length) == 0);
            if (block.Open != null)
            {
                int end = text.IndexOf(block.Close, i + block.Open.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + block.Close.Length;
                Emit(TokenKind.Comment, i, end);
                i = end;
                continue;
            }
            char c = text[i];
            // 文字列（同じ行の中で閉じる。\ のエスケープを考える）
            if (Array.IndexOf(lang.Quotes, c) >= 0)
            {
                int j = i + 1;
                while (j < text.Length && text[j] != c && text[j] != '\n') j += text[j] == '\\' && j + 1 < text.Length ? 2 : 1;
                int end = Math.Min(text.Length, j < text.Length && text[j] == c ? j + 1 : j);
                Emit(TokenKind.String, i, end);
                i = end;
                continue;
            }
            if (char.IsDigit(c) && (i == 0 || !IsWord(text[i - 1])))
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '.' or '_')) j++;
                Emit(TokenKind.Number, i, j);
                i = j;
                continue;
            }
            if (IsWord(c))
            {
                int j = i;
                while (j < text.Length && IsWord(text[j])) j++;
                string word = text[i..j];
                if (lang.Keywords.Contains(word)) Emit(TokenKind.Keyword, i, j);
                else plain.Append(word);
                i = j;
                continue;
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return tokens;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
