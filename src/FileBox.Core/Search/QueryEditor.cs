namespace FileBox.Core.Search;

/// <summary>検索バーのチップ操作をクエリ文字列へ反映する（チップ⇔テキストの双方向同期）。</summary>
public static class QueryEditor
{
    public static string SetKind(string query, KindFilter kind)
    {
        // folder:xxx の値部分は検索語として残す
        var parts = new List<string>();
        foreach (var t in QueryParser.Tokenize(query))
        {
            string raw = query.Substring(t.Start, t.Length);
            if (t.Key is "folder" or "file")
            {
                if (t.Value.Length > 0) parts.Add(t.Quoted ? $"\"{t.Value}\"" : t.Value);
                continue;
            }
            parts.Add(raw);
        }
        if (kind == KindFilter.Folders) parts.Insert(0, "folder:");
        else if (kind == KindFilter.Files) parts.Insert(0, "file:");
        return string.Join(' ', parts);
    }

    public static string SetMode(string query, MatchMode mode)
    {
        string result = RemoveKey(query, "mode");
        return mode == MatchMode.Substring ? result : Prepend(result, $"mode:{QueryParser.ModeKeyword(mode)}");
    }

    public static string SetInPath(string query, string? path)
    {
        string result = RemoveKey(query, "in");
        return string.IsNullOrEmpty(path) ? result : Append(result, $"in:\"{path}\"");
    }

    /// <summary>key:value を1つだけ持つように置き換える（value が空なら削除）。ext: / modified: のチップ用。</summary>
    public static string SetValue(string query, string key, string? value)
    {
        string result = RemoveKey(query, key);
        if (key == "modified") result = RemoveKey(result, "dm");
        return string.IsNullOrEmpty(value) ? result : Append(result, $"{key}:{value}");
    }

    public static string RemoveKey(string query, string key)
    {
        var parts = QueryParser.Tokenize(query)
            .Where(t => t.Key != key)
            .Select(t => query.Substring(t.Start, t.Length));
        return string.Join(' ', parts);
    }

    static string Prepend(string query, string token) => query.Length == 0 ? token : token + " " + query;
    static string Append(string query, string token) => query.Length == 0 ? token : query + " " + token;
}
