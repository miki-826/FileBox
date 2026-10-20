namespace FileBox.Core.Search;

/// <summary>
/// 種類で探すときの 1 つの種類（「ゲーム」「メモ」など）。
/// 名前に関係する語を含む項目、ゲームのライブラリの直下のフォルダ、印になる項目（.obsidian など）を含むフォルダを探す。
/// </summary>
public sealed class Concept
{
    public required string Name { get; init; }
    /// <summary>この語だけで検索したら、この種類として探す（ひらがな・カタカナ・大文字小文字は区別しない）。</summary>
    public required string[] Aliases { get; init; }
    /// <summary>名前にこの語を含む項目。</summary>
    public string[] Words { get; init; } = Array.Empty<string>();
    /// <summary>Words で探す対象（フォルダだけ・ファイルだけ・両方）。</summary>
    public KindFilter Kind { get; init; } = KindFilter.All;
    /// <summary>この場所の直下のフォルダが 1 件ずつ当たり（ゲームのライブラリなど）。パスの末尾で比べる。</summary>
    public (string Suffix, string Label)[] Libraries { get; init; } = Array.Empty<(string, string)>();
    /// <summary>この名前の項目を含むフォルダが当たり（.obsidian → Obsidian の保管庫、.git → リポジトリ）。</summary>
    public (string Name, string Label)[] Markers { get; init; } = Array.Empty<(string, string)>();
    /// <summary>この拡張子のファイルは名前に関係なく当たり。</summary>
    public string[] Extensions { get; init; } = Array.Empty<string>();

    /// <summary>索引に投げるクエリ（Words・Libraries・Markers・Extensions ごとに 1 つ）。</summary>
    public IEnumerable<string> IndexQueries()
    {
        if (Words.Length > 0)
        {
            string prefix = Kind switch { KindFilter.Folders => "folder: ", KindFilter.Files => "file: ", _ => "" };
            yield return prefix + string.Join(" | ", Words.Select(Quote));
        }
        if (Libraries.Length > 0)
            yield return "folder: " + string.Join(" | ", Libraries.Select(l =>
            {
                var parts = l.Suffix.Split('\\');
                string term = $"\"{parts[^1]}\"";
                return parts.Length > 1 ? $"{term} path:{Quote(string.Join('\\', parts[..^1]))}" : term;
            }));
        if (Markers.Length > 0)
            yield return string.Join(" | ", Markers.Select(m => $"\"{m.Name}\""));
        if (Extensions.Length > 0)
            yield return "file: ext:" + string.Join(',', Extensions);
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>ライブラリの場所なら、その名前（Steam など）。</summary>
    public string? LibraryLabel(string path)
    {
        string p = path.TrimEnd('\\');
        foreach (var (suffix, label) in Libraries)
            if (p.EndsWith("\\" + suffix, StringComparison.OrdinalIgnoreCase)) return label;
        return null;
    }

    /// <summary>ライブラリの中のゲームのさらに奥（ゲーム本体のデータ）か。名前の一致からは外す。</summary>
    public bool IsInsideLibrary(string path)
    {
        foreach (var (suffix, _) in Libraries)
        {
            int i = path.IndexOf("\\" + suffix + "\\", StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && path.IndexOf('\\', i + suffix.Length + 2) >= 0) return true;
        }
        return false;
    }

    /// <summary>印になる項目なら、それを含むフォルダが何か（Obsidian の保管庫 など）。</summary>
    public string? MarkerLabel(string name)
    {
        foreach (var (marker, label) in Markers)
            if (string.Equals(name, marker, StringComparison.OrdinalIgnoreCase)) return label;
        return null;
    }

    static readonly string[] NoiseFolders =
    {
        "\\node_modules\\", "\\.cache\\", "\\.git\\", "\\.nuget\\", "\\site-packages\\", "\\__pycache__\\", "\\packagecache\\",
        "\\obj\\", "\\.vs\\", "\\.vscode\\", "\\.gradle\\", "\\vendor\\", "\\winsxs\\",
    };

    static readonly HashSet<string> NoiseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".map", ".meta", ".cs", ".py", ".pyc", ".h", ".c", ".cpp", ".java", ".class",
        ".json", ".xml", ".yml", ".yaml", ".lock", ".dll", ".so", ".o", ".obj", ".pdb", ".resx", ".css", ".scss", ".mui", ".mof", ".manifest",
    };

    /// <summary>
    /// 名前で当たった項目の並び順の点数（大きいほど上）。語と名前がぴったり ＞ 語で始まる ＞ 含む。
    /// Windows・AppData・Program Files の奥にあるもの（設定やキャッシュ）は下げる。
    /// </summary>
    public double Score(string name, string path, bool isDirectory)
    {
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string n = ConceptSearch.Normalize(stem);
        double best = 0;
        foreach (var word in Words)
        {
            string w = ConceptSearch.Normalize(word);
            if (w.Length == 0 || !n.Contains(w)) continue;
            double s = n == w || n == w + "s" ? 1.0 : n.StartsWith(w) || n.EndsWith(w) ? 0.8 : 0.6;
            best = Math.Max(best, s);
        }
        if (Extensions.Any(e => name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))) best = Math.Max(best, 0.7);
        if (best == 0) return 0;
        // 開発の部品・キャッシュ（node_modules の note.js など）は人が書いたメモや書類ではないので出さない
        string p = path.ToLowerInvariant();
        if (NoiseFolders.Any(p.Contains)) return 0;
        if (!isDirectory && NoiseExtensions.Contains(Path.GetExtension(name))) return 0;
        if (Kind == KindFilter.Folders && isDirectory) best += 0.1;
        if (p.Contains("\\windows\\") || p.Contains("\\appdata\\") || p.Contains("\\$recycle.bin\\") || p.Contains("\\programdata\\")) best -= 0.5;
        else if (p.Contains("\\program files")) best -= 0.2;
        // 浅い場所にあるほど少しだけ上に
        best -= Math.Min(0.15, path.Count(c => c == '\\') * 0.01);
        return best;
    }
}

/// <summary>種類で探す（02_機能設計 §3 の拡張）。「ゲーム」と検索すると PC のゲームのフォルダ、「メモ」ならメモらしいファイルとフォルダを出す。</summary>
public static class ConceptSearch
{
    static readonly (string, string)[] GameLibraries =
    {
        (@"steamapps\common", "Steam"), ("Epic Games", "Epic Games"), ("Riot Games", "Riot Games"), ("XboxGames", "Xbox"),
        (@"GOG Galaxy\Games", "GOG"), ("GOG Games", "GOG"), (@"Ubisoft Game Launcher\games", "Ubisoft"),
        ("EA Games", "EA"), ("Origin Games", "EA"), ("Rockstar Games", "Rockstar Games"),
    };

    public static readonly IReadOnlyList<Concept> All = new Concept[]
    {
        new() { Name = "ゲーム", Aliases = new[] { "ゲーム", "game", "games", "げーむ" }, Words = new[] { "ゲーム", "game" }, Kind = KindFilter.Folders, Libraries = GameLibraries },
        new() { Name = "メモ", Aliases = new[] { "メモ", "memo", "めも", "ノート", "note", "notes", "メモ帳" },
                Words = new[] { "メモ", "memo", "ノート", "note", "備忘", "覚え書", "覚書", "todo" }, Markers = new[] { (".obsidian", "Obsidian の保管庫") } },
        new() { Name = "写真", Aliases = new[] { "写真", "photo", "photos", "画像", "picture", "pictures", "しゃしん", "フォト" },
                Words = new[] { "写真", "photo", "picture", "画像", "DCIM", "camera", "カメラ", "アルバム", "album" }, Kind = KindFilter.Folders },
        new() { Name = "スクリーンショット", Aliases = new[] { "スクショ", "スクリーンショット", "screenshot", "screenshots", "キャプチャ" },
                Words = new[] { "screenshot", "スクリーンショット", "スクショ", "キャプチャ", "capture" } },
        new() { Name = "動画", Aliases = new[] { "動画", "video", "videos", "ムービー", "movie", "movies", "ビデオ" },
                Words = new[] { "動画", "video", "movie", "ムービー", "ビデオ", "録画" }, Kind = KindFilter.Folders },
        new() { Name = "音楽", Aliases = new[] { "音楽", "music", "曲", "ミュージック", "おんがく" }, Words = new[] { "音楽", "music", "ミュージック", "iTunes" }, Kind = KindFilter.Folders },
        new() { Name = "書類", Aliases = new[] { "書類", "文書", "ドキュメント", "document", "documents" }, Words = new[] { "書類", "文書", "ドキュメント", "document" }, Kind = KindFilter.Folders },
        new() { Name = "請求書", Aliases = new[] { "請求書", "請求", "invoice", "せいきゅうしょ" }, Words = new[] { "請求", "invoice" } },
        new() { Name = "見積書", Aliases = new[] { "見積", "見積書", "見積もり", "見積り", "estimate", "quotation" }, Words = new[] { "見積", "estimate", "quotation" } },
        new() { Name = "契約書", Aliases = new[] { "契約", "契約書", "contract" }, Words = new[] { "契約", "contract", "agreement" } },
        new() { Name = "履歴書", Aliases = new[] { "履歴書", "職務経歴書", "resume", "cv" }, Words = new[] { "履歴書", "職務経歴", "経歴書", "resume" } },
        new() { Name = "議事録", Aliases = new[] { "議事録", "minutes", "会議" }, Words = new[] { "議事録", "minutes", "打ち合わせ", "打合せ", "ミーティング", "meeting" } },
        new() { Name = "課題・レポート", Aliases = new[] { "課題", "宿題", "レポート", "report", "assignment" }, Words = new[] { "課題", "宿題", "レポート", "report", "assignment" } },
        new() { Name = "プログラム", Aliases = new[] { "コード", "ソース", "プログラム", "開発", "リポジトリ", "repo", "repos", "source", "code", "git" },
                Words = new[] { "source", "repos", "projects", "開発", "ソース" }, Kind = KindFilter.Folders, Markers = new[] { (".git", "Git のリポジトリ") } },
        new() { Name = "インストーラー", Aliases = new[] { "インストーラー", "インストーラ", "installer", "setup", "セットアップ" },
                Words = new[] { "setup", "install", "インストーラ" }, Kind = KindFilter.Files, Extensions = new[] { "msi", "msix", "appx" } },
        new() { Name = "壁紙", Aliases = new[] { "壁紙", "wallpaper", "wallpapers", "背景" }, Words = new[] { "壁紙", "wallpaper", "背景" } },
        new() { Name = "バックアップ", Aliases = new[] { "バックアップ", "backup", "backups" }, Words = new[] { "backup", "バックアップ" } },
    };

    /// <summary>比べるための形（小文字・カタカナはひらがな・全角英数は半角）。</summary>
    public static string Normalize(string s) =>
        KanaConverter.KatakanaToHiragana(s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant().Trim());

    /// <summary>クエリが種類の語 1 つだけ（「ゲーム」「kind:メモ」など、ほかの条件なし）なら、その種類。</summary>
    public static Concept? Find(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var tokens = QueryParser.Tokenize(query.Trim());
        if (tokens.Count != 1 || tokens[0].Key != null) return null;
        string word = tokens[0].Value;
        int colon = word.IndexOf(':');
        if (colon > 0)
        {
            // kind: / 種類: で明示した場合
            if (word[..colon].ToLowerInvariant() is not ("kind" or "種類")) return null;
            word = word[(colon + 1)..];
        }
        string n = Normalize(word);
        if (n.Length == 0) return null;
        return All.FirstOrDefault(c => c.Aliases.Any(a => Normalize(a) == n));
    }
}
