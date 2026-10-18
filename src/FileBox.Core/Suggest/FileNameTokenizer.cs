using System.Text;
using System.Text.RegularExpressions;
using FileBox.Core.Search;

namespace FileBox.Core.Suggest;

/// <summary>ファイル名を分解した結果。Words は区切りで分けた語、Grams は日本語の文字 2-gram。</summary>
public sealed record TokenizedName(
    string Original,
    IReadOnlyList<string> Words,
    IReadOnlyList<string> DisplayWords,
    IReadOnlyList<string> Grams,
    int? Year,
    int? Month,
    string Ext)
{
    /// <summary>学習履歴に保存する形（語のみ、スペース区切り）。</summary>
    public string Key => string.Join(' ', Words);
}

/// <summary>
/// 保存先推定用のトークナイザ（02_機能設計 §1.3-1）。形態素解析は使わず、
/// 区切り文字・文字種境界・CamelCase で分割し、日本語は文字 2-gram を併用する。
/// </summary>
public static partial class FileNameTokenizer
{
    /// <summary>どのフォルダにも現れやすく手掛かりにならない語。</summary>
    static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "copy", "こぴー", "final", "最終", "さいしゅう", "new", "新規", "新しい", "untitled", "無題", "document", "doc",
        "file", "files", "img", "image", "images", "dsc", "scan", "ver", "rev", "修正", "版", "the", "and", "for", "のこぴー", "draft", "tmp", "temp",
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "exe", "msi", "setup", "install", "installer", "x64", "x86", "win", "win32", "win64", "bit", "release", "build", "alpha", "beta",
        "screenshot", "すくりーんしょっと", "download", "だうんろーど", "output", "export", "data", "test", "sample",
    };

    public static TokenizedName Tokenize(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        string stem = ext.Length > 0 && ext.Length <= 8 ? Path.GetFileNameWithoutExtension(fileName) : fileName;
        if (ext.Length > 8) ext = "";

        // 表示用（NFKC のみ）と比較用（小文字・ひらがな化）は同じ長さなので、位置で対応付けられる
        string display = stem.Normalize(NormalizationForm.FormKC);
        string text = KanaConverter.KatakanaToHiragana(display.ToLowerInvariant());
        if (text.Length != display.Length) display = text;
        var (year, month, rest) = ExtractDate(text);

        var words = new List<string>();
        var displayWords = new List<string>();
        foreach (var (start, length) in SplitSegments(rest, display))
        {
            string segment = rest.Substring(start, length);
            if (segment.All(char.IsDigit)) continue;
            if (segment.Length == 1 && !IsKanji(segment[0])) continue;
            if (segment.Length <= 2 && segment.All(char.IsAsciiLetter)) continue;   // 英字2文字以下（pm, xr）は手掛かりにならない
            if (StopWords.Contains(segment) || words.Contains(segment)) continue;
            words.Add(segment);
            displayWords.Add(display.Substring(start, length));
        }

        // CamelCase で分けた英字の連なり（ChatGPT → chat / gpt）は、つながった形も語として持つ
        foreach (Match m in LatinRun().Matches(display))
        {
            string run = m.Value;
            string lower = run.ToLowerInvariant();
            bool camel = run.Skip(1).Any(char.IsUpper) && run.Any(char.IsLower);
            if (!camel || run.Length < 4 || words.Contains(lower) || StopWords.Contains(lower)) continue;
            words.Add(lower);
            displayWords.Add(run);
        }

        var grams = new List<string>();
        foreach (var w in words)
        {
            if (w.Length < 3 || !w.Any(IsCjk)) continue;
            for (int i = 0; i + 2 <= w.Length; i++)
            {
                string g = w.Substring(i, 2);
                if (!grams.Contains(g) && !words.Contains(g)) grams.Add(g);
            }
        }
        return new TokenizedName(fileName, words, displayWords, grams, year, month, ext);
    }

    /// <summary>NFKC（全角英数→半角）＋小文字化＋カタカナ→ひらがな。</summary>
    public static string Normalize(string s) =>
        KanaConverter.KatakanaToHiragana(s.Normalize(NormalizationForm.FormKC).ToLowerInvariant());

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex LatinRun();

    /// <summary>フォルダ名を語に分ける（末尾の「.xxx」を拡張子扱いしない）。</summary>
    public static IReadOnlyList<string> FolderWords(string folderName) => Tokenize(folderName + ".x").Words;

    // ---------------------------------------------------------------- 日付

    [GeneratedRegex(@"(?<!\d)(20\d{2})(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex Ymd8();

    [GeneratedRegex(@"(?<!\d)(20\d{2})\s*[-_./年]\s*(0?[1-9]|1[0-2])(?!\d)\s*月?(?:\s*[-_./]?\s*(?:0?[1-9]|[12]\d|3[01])(?!\d)\s*日?)?")]
    private static partial Regex YearMonth();

    [GeneratedRegex(@"(?:令和|れいわ|(?<![a-z])r)\s*(\d{1,2}|元)\s*年?(?:\s*(\d{1,2})\s*月)?")]
    private static partial Regex Reiwa();

    [GeneratedRegex(@"(?<!\d)(20\d{2})\s*年?(?!\d)")]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"(?<!\d)(0?[1-9]|1[0-2])\s*月")]
    private static partial Regex MonthOnly();

    internal static (int? Year, int? Month, string Text) ExtractDate(string text)
    {
        int? year = null, month = null;
        string Take(Regex re, Func<Match, bool> apply)
        {
            var m = re.Match(text);
            if (!m.Success || !apply(m)) return text;
            // 同じ長さの空白で置き換えて、表示用文字列との位置対応を保つ
            return text.Remove(m.Index, m.Length).Insert(m.Index, new string(' ', m.Length));
        }

        text = Take(Ymd8(), m => { year = int.Parse(m.Groups[1].Value); month = int.Parse(m.Groups[2].Value); return true; });
        if (year == null)
            text = Take(YearMonth(), m => { year = int.Parse(m.Groups[1].Value); month = int.Parse(m.Groups[2].Value); return true; });
        if (year == null)
            text = Take(Reiwa(), m =>
            {
                int n = m.Groups[1].Value == "元" ? 1 : int.Parse(m.Groups[1].Value);
                if (n is < 1 or > 40) return false;
                year = 2018 + n;
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int mo) && mo is >= 1 and <= 12) month = mo;
                return true;
            });
        if (year == null)
            text = Take(YearOnly(), m => { year = int.Parse(m.Groups[1].Value); return true; });
        if (month == null)
            text = Take(MonthOnly(), m => { month = int.Parse(m.Groups[1].Value); return true; });
        return (year, month, text);
    }

    // ---------------------------------------------------------------- 分割

    /// <summary>語の (開始位置, 長さ) を返す。CamelCase の境界は大文字小文字を残した <paramref name="cased"/> で判定する。</summary>
    static IEnumerable<(int Start, int Length)> SplitSegments(string text, string cased)
    {
        int start = -1;
        CharKind prev = CharKind.Separator;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            var kind = Kind(c);
            // 「ABC商事」「NTT東日本」のような大文字略語＋漢字は1語のまま
            bool acronymJoin = prev == CharKind.Latin && kind == CharKind.Kanji && start >= 0
                && cased.AsSpan(start, i - start).ToString().All(char.IsUpper);
            bool boundary = kind == CharKind.Separator
                || (prev != CharKind.Separator && kind != prev && !acronymJoin && !(prev == CharKind.Kanji && kind == CharKind.Hiragana && IsOkurigana(c)))
                || (kind == CharKind.Latin && i > 0 && char.IsUpper(cased[i]) && char.IsLower(cased[i - 1]));
            if (boundary && start >= 0)
            {
                yield return (start, i - start);
                start = -1;
            }
            if (kind != CharKind.Separator && start < 0) start = i;
            prev = kind;
        }
        if (start >= 0) yield return (start, text.Length - start);
    }

    // 漢字に続く送り仮名（「見積り」「申し込み」）は同じ語として扱う
    static bool IsOkurigana(char c) => "りしみきいけちえ".Contains(c);

    enum CharKind { Separator, Latin, Digit, Kanji, Hiragana, Other }

    static CharKind Kind(char c)
    {
        if (char.IsAsciiLetter(c)) return CharKind.Latin;
        if (char.IsDigit(c)) return CharKind.Digit;
        if (IsKanji(c)) return CharKind.Kanji;
        if (c is >= 'ぁ' and <= 'ゟ' || c == 'ー') return CharKind.Hiragana;
        if (char.IsLetter(c)) return CharKind.Other;
        return CharKind.Separator;
    }

    static bool IsKanji(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or '々';

    static bool IsCjk(char c) => IsKanji(c) || c is >= 'ぁ' and <= 'ヿ';
}
