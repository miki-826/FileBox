using FileBox.Core.Search;

namespace FileBox.Core.Files;

/// <summary>
/// 一覧でキーを打ったときの頭出し（エクスプローラーと同じ）。続けて打つと前方一致の文字列が伸び、
/// 同じ 1 文字を繰り返すと、その文字で始まる次の項目へ進む。ひらがなとカタカナ、全角と半角は区別しない。
/// </summary>
public sealed class TypeAhead(TimeSpan? timeout = null)
{
    readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(1);
    string _buffer = "";
    DateTime _last = DateTime.MinValue;

    public string Buffer => _buffer;

    /// <summary>文字 c を受け取り、選ぶべき位置を返す（見つからなければ -1）。current は今選ばれている位置。</summary>
    public int Next(char c, IReadOnlyList<string> names, int current, DateTime now)
    {
        if (char.IsControl(c)) return -1;
        if (now - _last > _timeout) _buffer = "";
        _last = now;
        _buffer += c;

        bool repeat = _buffer.Length > 1 && _buffer.All(ch => ch == _buffer[0]);
        string prefix = repeat ? _buffer[..1] : _buffer;
        // 続けて打っているときは今の項目も候補（「re」→「rep」で同じ項目のまま）、新しく打ち始めたら次から
        int from = _buffer.Length == 1 || repeat ? current + 1 : Math.Max(current, 0);
        return Find(names, prefix, from);
    }

    /// <summary>続けて打っている途中か（Space を頭出しの文字として扱うかの判断）。</summary>
    public bool IsTyping(DateTime now) => _buffer.Length > 0 && now - _last <= _timeout;

    public void Reset() => _buffer = "";

    public static int Find(IReadOnlyList<string> names, string prefix, int from)
    {
        if (names.Count == 0) return -1;
        string p = Normalize(prefix);
        for (int k = 0; k < names.Count; k++)
        {
            int i = ((from % names.Count) + names.Count + k) % names.Count;
            if (Normalize(names[i]).StartsWith(p, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    static string Normalize(string s) => KanaConverter.KatakanaToHiragana(s.Normalize(System.Text.NormalizationForm.FormKC));
}
