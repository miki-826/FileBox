using System.Text;

namespace FileBox.Core.Search;

/// <summary>あいまい検索用のローマ字⇔かな変換（02_機能設計 §3.3「あいまい」）。</summary>
public static class KanaConverter
{
    static readonly Dictionary<string, string> Romaji = BuildTable();

    /// <summary>ローマ字をひらがなに変換する。変換できない文字はそのまま残す。</summary>
    public static string RomajiToHiragana(string input)
    {
        string s = input.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            // 促音: 同じ子音の連続 (n 以外)
            if (i + 1 < s.Length && c == s[i + 1] && IsConsonant(c) && c != 'n')
            {
                sb.Append('っ');
                i++;
                continue;
            }
            // 撥音: nn / n' / n + 子音(y 以外) / 末尾の n
            if (c == 'n')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    sb.Append('ん');
                    i += 2;
                    continue;
                }
                if (i + 1 < s.Length && s[i + 1] == 'n')
                {
                    // "konnichiha" → こん + にちは（2つ目の n は次の音節に使う）
                    bool nextIsSyllable = i + 2 < s.Length && "aiueoy".IndexOf(s[i + 2]) >= 0;
                    sb.Append('ん');
                    i += nextIsSyllable ? 1 : 2;
                    continue;
                }
                if (i + 1 == s.Length || (IsConsonant(s[i + 1]) && s[i + 1] != 'y'))
                {
                    sb.Append('ん');
                    i++;
                    continue;
                }
            }
            bool matched = false;
            for (int len = 3; len >= 1; len--)
            {
                if (i + len > s.Length) continue;
                if (Romaji.TryGetValue(s.Substring(i, len), out var kana))
                {
                    sb.Append(kana);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                sb.Append(c == '-' ? 'ー' : c);
                i++;
            }
        }
        return sb.ToString();
    }

    public static string HiraganaToKatakana(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'ぁ' and <= 'ゖ') chars[i] = (char)(chars[i] + 0x60);
        return new string(chars);
    }

    public static string KatakanaToHiragana(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'ァ' and <= 'ヶ') chars[i] = (char)(chars[i] - 0x60);
        return new string(chars);
    }

    /// <summary>入力文字列から、あいまい一致に使う表記ゆれの候補を列挙する。</summary>
    public static IReadOnlyList<string> FuzzyVariants(string input)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { input };
        string normalized = input.Normalize(NormalizationForm.FormKC);
        set.Add(normalized);
        set.Add(KatakanaToHiragana(normalized));
        set.Add(HiraganaToKatakana(normalized));
        if (normalized.Any(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
        {
            string hira = RomajiToHiragana(normalized);
            if (!hira.Any(ch => ch is >= 'a' and <= 'z'))
            {
                set.Add(hira);
                set.Add(HiraganaToKatakana(hira));
                // 長音のゆれ: "seikyuu" → せいきゅう / せいきゅー
                if (hira.Contains("うう")) set.Add(hira.Replace("うう", "う"));
            }
        }
        set.Remove("");
        return set.ToList();
    }

    static bool IsConsonant(char c) => c is >= 'a' and <= 'z' && "aiueo".IndexOf(c) < 0;

    static Dictionary<string, string> BuildTable()
    {
        var t = new Dictionary<string, string>
        {
            ["a"] = "あ", ["i"] = "い", ["u"] = "う", ["e"] = "え", ["o"] = "お",
            ["ka"] = "か", ["ki"] = "き", ["ku"] = "く", ["ke"] = "け", ["ko"] = "こ",
            ["sa"] = "さ", ["si"] = "し", ["shi"] = "し", ["su"] = "す", ["se"] = "せ", ["so"] = "そ",
            ["ta"] = "た", ["ti"] = "ち", ["chi"] = "ち", ["tu"] = "つ", ["tsu"] = "つ", ["te"] = "て", ["to"] = "と",
            ["na"] = "な", ["ni"] = "に", ["nu"] = "ぬ", ["ne"] = "ね", ["no"] = "の",
            ["ha"] = "は", ["hi"] = "ひ", ["hu"] = "ふ", ["fu"] = "ふ", ["he"] = "へ", ["ho"] = "ほ",
            ["ma"] = "ま", ["mi"] = "み", ["mu"] = "む", ["me"] = "め", ["mo"] = "も",
            ["ya"] = "や", ["yu"] = "ゆ", ["yo"] = "よ",
            ["ra"] = "ら", ["ri"] = "り", ["ru"] = "る", ["re"] = "れ", ["ro"] = "ろ",
            ["la"] = "ら", ["li"] = "り", ["lu"] = "る", ["le"] = "れ", ["lo"] = "ろ",
            ["wa"] = "わ", ["wo"] = "を", ["n"] = "ん",
            ["ga"] = "が", ["gi"] = "ぎ", ["gu"] = "ぐ", ["ge"] = "げ", ["go"] = "ご",
            ["za"] = "ざ", ["zi"] = "じ", ["ji"] = "じ", ["zu"] = "ず", ["ze"] = "ぜ", ["zo"] = "ぞ",
            ["da"] = "だ", ["di"] = "ぢ", ["du"] = "づ", ["de"] = "で", ["do"] = "ど",
            ["ba"] = "ば", ["bi"] = "び", ["bu"] = "ぶ", ["be"] = "べ", ["bo"] = "ぼ",
            ["pa"] = "ぱ", ["pi"] = "ぴ", ["pu"] = "ぷ", ["pe"] = "ぺ", ["po"] = "ぽ",
            ["va"] = "ゔぁ", ["vi"] = "ゔぃ", ["vu"] = "ゔ", ["ve"] = "ゔぇ", ["vo"] = "ゔぉ",
            ["fa"] = "ふぁ", ["fi"] = "ふぃ", ["fe"] = "ふぇ", ["fo"] = "ふぉ",
            ["ja"] = "じゃ", ["ju"] = "じゅ", ["je"] = "じぇ", ["jo"] = "じょ",
            ["sha"] = "しゃ", ["shu"] = "しゅ", ["she"] = "しぇ", ["sho"] = "しょ",
            ["cha"] = "ちゃ", ["chu"] = "ちゅ", ["che"] = "ちぇ", ["cho"] = "ちょ",
            ["tha"] = "てゃ", ["thi"] = "てぃ", ["thu"] = "てゅ",
            ["xa"] = "ぁ", ["xi"] = "ぃ", ["xu"] = "ぅ", ["xe"] = "ぇ", ["xo"] = "ぉ", ["xtu"] = "っ", ["xtsu"] = "っ",
        };
        foreach (var (c, row) in new[]
                 {
                     ("k", "き"), ("s", "し"), ("t", "ち"), ("n", "に"), ("h", "ひ"), ("m", "み"), ("r", "り"),
                     ("g", "ぎ"), ("z", "じ"), ("d", "ぢ"), ("b", "び"), ("p", "ぴ"), ("j", "じ"),
                 })
        {
            t[c + "ya"] = row + "ゃ";
            t[c + "yu"] = row + "ゅ";
            t[c + "yo"] = row + "ょ";
        }
        return t;
    }
}
