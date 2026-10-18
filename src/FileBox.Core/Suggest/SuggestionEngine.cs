using System.Text.RegularExpressions;

namespace FileBox.Core.Suggest;

/// <summary>候補フォルダの中身（直下のファイル名・サブフォルダ名）。</summary>
public sealed record FolderInfo(string Path, IReadOnlyList<string> FileNames, IReadOnlyList<string> SubfolderNames, DateTime? LastWriteUtc);

/// <summary>候補フォルダの取得元。App では Indexer（名前索引）＋実ファイルシステム。</summary>
public interface IFolderSource
{
    /// <summary>語を名前に含むファイル・フォルダから候補フォルダを集める（02_機能設計 §1.3-2）。</summary>
    Task<IReadOnlyList<FolderInfo>> FindCandidatesAsync(IReadOnlyList<string> terms, CancellationToken ct);

    /// <summary>1フォルダの中身。存在しなければ null。</summary>
    FolderInfo? GetFolder(string path);
}

/// <summary>確定した保存先の履歴1件（save_history）。RejectedPath は提案1位だったが選ばれなかったフォルダ。</summary>
public sealed record SaveRecord(IReadOnlyList<string> Words, string? Ext, string FolderPath, string? RejectedPath, DateTime ChosenAt);

public enum Confidence { Low, Medium, High }

public sealed record FolderSuggestion(string Path, double Score, Confidence Confidence, string Reason, bool IsNew = false)
{
    public string ConfidenceText => Confidence switch { Confidence.High => "高", Confidence.Medium => "中", _ => "低" };
}

public sealed record SuggestionResult(TokenizedName Name, IReadOnlyList<FolderSuggestion> Candidates, FolderSuggestion? NewFolder)
{
    public FolderSuggestion? Best => Candidates.Count > 0 ? Candidates[0] : NewFolder;
}

/// <summary>スコアの重み（02_機能設計 §1.3-3、設定で調整可能）。</summary>
public sealed class SuggestionWeights
{
    public double Name { get; set; } = 0.30;
    public double Learned { get; set; } = 0.40;
    public double FolderName { get; set; } = 0.10;
    public double Ext { get; set; } = 0.10;
    public double Recency { get; set; } = 0.10;
}

/// <summary>
/// 保存先推定エンジン（02_機能設計 §1.3〜1.5）。外部 API を使わずローカル情報だけで、
/// 候補フォルダ上位3件と「なさそうなら新規フォルダ」の提案を返す。
/// </summary>
public sealed partial class SuggestionEngine
{
    const int MaxCandidates = 3;
    const int ProfileFileLimit = 400;
    const double MinScore = 0.1;

    readonly IFolderSource _source;
    readonly Func<IReadOnlyList<SaveRecord>> _history;
    readonly SuggestionWeights _weights;
    readonly Func<DateTime> _clock;

    /// <summary>候補が1つも無いときの新規フォルダの置き場所（既定: ドキュメント）。</summary>
    public string? FallbackRoot { get; set; }

    /// <summary>保存場所として自然な場所（ドキュメント・デスクトップ・データ用ドライブなど）。配下の候補を少し優遇する。</summary>
    public IReadOnlyList<string> PreferredRoots { get; set; } = Array.Empty<string>();

    public SuggestionEngine(IFolderSource source, Func<IReadOnlyList<SaveRecord>> history, SuggestionWeights? weights = null, Func<DateTime>? clock = null)
    {
        _source = source;
        _history = history;
        _weights = weights ?? new SuggestionWeights();
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>保存先候補にしないフォルダ（システム・アプリ内部・開発用の生成物・ドットで始まる隠しフォルダ）。</summary>
    public static bool IsExcluded(string path)
    {
        string p = path.Replace('/', '\\').TrimEnd('\\') + "\\";
        if (p.Contains("\\.", StringComparison.Ordinal)) return true;   // .git / .nuget / .vscode など
        foreach (var part in ExcludedParts)
            if (p.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static readonly string[] ExcludedParts =
    {
        ":\\Windows\\", ":\\Program Files\\", ":\\Program Files (x86)\\", ":\\ProgramData\\", "\\AppData\\", "\\$Recycle.Bin\\",
        ":\\System Volume Information\\", ":\\Recovery\\", ":\\Users\\Default\\", ":\\Users\\Public\\", ":\\Users\\All Users\\", "\\$WinREAgent\\", "\\Temp\\", "\\tmp\\",
        "\\node_modules\\", "\\obj\\", "\\bin\\", "\\__pycache__\\", "\\venv\\", "\\site-packages\\", "\\vendor\\",
        "\\Library\\PackageCache\\", "\\Library\\Bee\\", "\\Library\\ScriptAssemblies\\", "\\Library\\ShaderCache\\", "\\Library\\BurstCache\\",
        "\\Library\\Artifacts\\", "\\Library\\StateCache\\", "\\Library\\il2cpp_cache\\", "\\Packages\\com.", "\\packages\\microsoft.", "\\Windows Kits\\",
        "\\build\\", "\\dist\\", "\\target\\debug\\", "\\target\\release\\",
    };

    public async Task<SuggestionResult> SuggestAsync(string fileName, string? currentFolder = null, CancellationToken ct = default)
    {
        var name = FileNameTokenizer.Tokenize(fileName);
        var history = _history();

        // ---- 候補収集（索引の名前一致 ＋ 履歴に出てくるフォルダ）
        var terms = name.Words.ToList();
        var found = terms.Count > 0 ? await _source.FindCandidatesAsync(terms, ct).ConfigureAwait(false) : Array.Empty<FolderInfo>();
        var byPath = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in found) byPath.TryAdd(Trim(f.Path), f);
        foreach (var path in RelevantHistoryFolders(name, history))
        {
            if (byPath.ContainsKey(path)) continue;
            if (_source.GetFolder(path) is { } info) byPath[path] = info;
        }
        if (currentFolder != null)
        {
            // 今ある場所と、その中（ダウンロードで展開したアーカイブなど）は候補にしない
            string cur = Trim(currentFolder);
            foreach (var key in byPath.Keys.Where(k => string.Equals(k, cur, StringComparison.OrdinalIgnoreCase)
                                                   || k.StartsWith(cur + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                byPath.Remove(key);
        }
        foreach (var key in byPath.Keys.Where(IsExcluded).ToList()) byPath.Remove(key);
        ct.ThrowIfCancellationRequested();

        // ---- スコアリング
        var scored = Score(name, byPath.Values.ToList(), history);
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        var top = new List<FolderSuggestion>();
        double s1 = scored.Count > 0 ? scored[0].Score : 0;
        double s2 = scored.Count > 1 ? scored[1].Score : 0;
        FolderSuggestion? newFolder = null;
        foreach (var c in scored)
        {
            if (top.Count >= MaxCandidates) break;
            if (c.Score < MinScore) break;   // 手掛かりがほぼ無い候補は出さない（新規フォルダ提案に任せる）
            var conf = top.Count == 0 ? Classify(s1, s2) : Classify(c.Score, 0) == Confidence.High ? Confidence.Medium : Classify(c.Score, 0);
            string path = c.Info.Path;
            string reason = c.Reason;

            // 日付サブフォルダの補正（2026\10月 のような規則があれば掘り下げる）
            var refined = RefineByDate(path, name);
            if (refined is { } r)
            {
                if (r.Exists)
                {
                    path = r.Path;
                    reason += "（日付フォルダ）";
                }
                else if (top.Count == 0)
                {
                    newFolder ??= new FolderSuggestion(r.Path, c.Score, conf, $"「{Leaf(path)}」の日付フォルダ規則に合わせて作成", IsNew: true);
                }
            }
            if (top.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            top.Add(new FolderSuggestion(path, Math.Round(c.Score, 3), conf, reason));
        }

        newFolder ??= ProposeNewFolder(name, scored, top);
        return new SuggestionResult(name, top, newFolder);
    }

    // ================================================================ スコア

    sealed record Scored(FolderInfo Info, double Score, string Reason);

    List<Scored> Score(TokenizedName name, List<FolderInfo> folders, IReadOnlyList<SaveRecord> history)
    {
        var result = new List<Scored>(folders.Count);
        if (folders.Count == 0) return result;

        // フォルダごとのプロファイル（直下ファイル名の語・2-gram の出現数）
        var profiles = folders.Select(f => BuildProfile(f.FileNames)).ToList();
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in profiles)
            foreach (var t in p.Terms.Keys) df[t] = df.GetValueOrDefault(t) + 1;
        int n = folders.Count;
        double Idf(string t) => Math.Log(1 + (double)n / (1 + df.GetValueOrDefault(t)));

        var query = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var w in name.Words) query[w] = Idf(w);
        foreach (var g in name.Grams) query.TryAdd(g, 0.5 * Idf(g));
        double qNorm = Math.Sqrt(query.Values.Sum(v => v * v));

        var learned = LearnedScores(name, history);
        string stem = FileNameTokenizer.Normalize(Path.GetFileNameWithoutExtension(name.Original));
        DateTime now = _clock().ToUniversalTime();

        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            var prof = profiles[i];

            // 名前類似度: TF-IDF コサイン
            double dot = 0, fNorm = 0;
            foreach (var (t, tf) in prof.Terms)
            {
                double w = (1 + Math.Log(tf)) * Idf(t);
                fNorm += w * w;
                if (query.TryGetValue(t, out double q)) dot += w * q;
            }
            double nameSim = qNorm > 0 && fNorm > 0 ? dot / (qNorm * Math.Sqrt(fNorm)) : 0;

            // フォルダ名一致: 自分の名前 1.0、祖先 0.5、2-gram は 0.3
            double folderName = FolderNameMatch(name, f.Path, out string? matchedWord);

            double ext = name.Ext.Length > 0 && f.FileNames.Count > 0
                ? (double)f.FileNames.Count(x => x.EndsWith("." + name.Ext, StringComparison.OrdinalIgnoreCase)) / f.FileNames.Count
                : 0;
            double recency = f.LastWriteUtc is { } lw ? Math.Exp(-Math.Max(0, (now - lw).TotalDays) / 90) : 0;
            double learn = learned.TryGetValue(Trim(f.Path), out var l) ? l : 0;

            double score = _weights.Name * nameSim + _weights.Learned * learn + _weights.FolderName * folderName
                         + _weights.Ext * ext + _weights.Recency * recency;
            // フォルダ名がファイル名（拡張子・日付を除く）と同じなら、ほぼその場所（TypeScript_01.zip → TypeScript_01）
            if (stem.Length > 0 && string.Equals(FileNameTokenizer.Normalize(Leaf(f.Path)), stem, StringComparison.Ordinal)) score += 0.1;
            // 語の手掛かりが何も無いフォルダ（拡張子・新しさだけ）は候補として弱くする
            if (nameSim < 0.01 && folderName < 0.01 && learn < 0.01) score *= 0.3;
            // 深すぎる階層は保存先らしくないので弱め、利用者のデータ置き場は少し優遇
            int depth = f.Path.Count(ch => ch == '\\');
            if (depth > 6) score *= Math.Pow(0.85, depth - 6);
            if (PreferredRoots.Any(r => IsSameOrUnder(f.Path, r))) score *= 1.2;
            score = Math.Max(0, score);

            string reason = learn >= 0.3 ? "以前この場所に保存しました"
                : folderName >= 0.5 && matchedWord != null ? $"フォルダ名が「{matchedWord}」に一致"
                : nameSim >= 0.05 ? $"似た名前のファイルが {prof.MatchCount(name.Words)} 件"
                : ext >= 0.5 ? $"同じ種類（.{name.Ext}）のファイルが多い"
                : "最近使ったフォルダ";
            result.Add(new Scored(f, score, reason));
        }
        return result;
    }

    static Confidence Classify(double s1, double s2)
    {
        if (s1 >= 0.40 && s1 - s2 >= 0.08) return Confidence.High;
        if (s1 >= 0.20) return Confidence.Medium;
        return Confidence.Low;
    }

    sealed class Profile
    {
        public readonly Dictionary<string, int> Terms = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyList<string>> FileWords = new();

        public int MatchCount(IReadOnlyList<string> words) =>
            FileWords.Count(fw => fw.Any(w => words.Contains(w)));
    }

    static Profile BuildProfile(IReadOnlyList<string> fileNames)
    {
        var p = new Profile();
        foreach (var file in fileNames.Take(ProfileFileLimit))
        {
            var t = FileNameTokenizer.Tokenize(file);
            p.FileWords.Add(t.Words);
            foreach (var w in t.Words) p.Terms[w] = p.Terms.GetValueOrDefault(w) + 1;
            foreach (var g in t.Grams) p.Terms[g] = p.Terms.GetValueOrDefault(g) + 1;
        }
        return p;
    }

    static double FolderNameMatch(TokenizedName name, string path, out string? matched)
    {
        matched = null;
        if (name.Words.Count == 0) return 0;
        var parts = Trim(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        var self = FolderTerms(parts[^1]);
        var ancestors = parts.Take(parts.Length - 1).Skip(1).TakeLast(3).Select(FolderTerms).ToArray();   // ドライブ名を除く直近3階層
        double total = 0;
        for (int i = 0; i < name.Words.Count; i++)
        {
            string w = name.Words[i];
            if (self.Matches(w))
            {
                total += 1.0;
                if (matched == null || name.DisplayWords[i].Length > matched.Length) matched = name.DisplayWords[i];
            }
            else if (ancestors.Any(a => a.Matches(w))) total += 0.5;
            else if (name.Grams.Any(g => w.Contains(g, StringComparison.Ordinal) && self.Text.Contains(g, StringComparison.Ordinal))) total += 0.3;
        }
        return Math.Min(1.0, total / Math.Min(name.Words.Count, 2));
    }

    /// <summary>フォルダ名の比較用。英字の語は語単位で一致（win が win-x64 に当たらない）、日本語は部分一致。</summary>
    sealed record FolderTermSet(string Text, IReadOnlyList<string> Words)
    {
        public bool Matches(string word) =>
            word.All(char.IsAscii) ? Words.Contains(word) : Text.Contains(word, StringComparison.Ordinal);
    }

    static FolderTermSet FolderTerms(string folderName) =>
        new(FileNameTokenizer.Normalize(folderName), FileNameTokenizer.FolderWords(folderName));

    static bool IsSameOrUnder(string path, string root)
    {
        string p = Trim(path), r = Trim(root);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ 学習（02_機能設計 §1.5）

    const double HalfLifeDays = 60;

    Dictionary<string, double> LearnedScores(TokenizedName name, IReadOnlyList<SaveRecord> history)
    {
        var raw = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        DateTime now = _clock();
        foreach (var h in history)
        {
            double sim = Similarity(name, h);
            if (sim <= 0) continue;
            double decay = Math.Pow(0.5, Math.Max(0, (now - h.ChosenAt).TotalDays) / HalfLifeDays);
            string folder = Trim(h.FolderPath);
            raw[folder] = raw.GetValueOrDefault(folder) + sim * decay;
            // 提示したが選ばれなかった1位は少し下げる（ネガティブ学習）
            if (h.RejectedPath is { } rej)
            {
                rej = Trim(rej);
                raw[rej] = raw.GetValueOrDefault(rej) - 0.5 * sim * decay;
            }
        }
        // 回数が増えるほど ±1 に近づく飽和関数（負は「何度も選ばれなかった」）
        return raw.ToDictionary(kv => kv.Key, kv => Math.Sign(kv.Value) * (1 - Math.Exp(-1.2 * Math.Abs(kv.Value))), StringComparer.OrdinalIgnoreCase);
    }

    static double Similarity(TokenizedName name, SaveRecord h)
    {
        bool sameExt = name.Ext.Length > 0 && string.Equals(name.Ext, h.Ext, StringComparison.OrdinalIgnoreCase);
        if (name.Words.Count == 0 || h.Words.Count == 0) return sameExt ? 0.2 : 0;
        int inter = name.Words.Count(w => h.Words.Contains(w));
        if (inter == 0) return 0;
        double jaccard = (double)inter / (name.Words.Count + h.Words.Count - inter);
        return jaccard * (sameExt ? 1.0 : 0.7);
    }

    IEnumerable<string> RelevantHistoryFolders(TokenizedName name, IReadOnlyList<SaveRecord> history) =>
        history.Where(h => Similarity(name, h) > 0)
               .GroupBy(h => Trim(h.FolderPath), StringComparer.OrdinalIgnoreCase)
               .OrderByDescending(g => g.Count())
               .Take(20)
               .Select(g => g.Key);

    // ================================================================ 日付サブフォルダ（§1.3-5）

    [GeneratedRegex(@"^(\D{0,4})(20\d{2})(年度?)?$")]
    private static partial Regex YearFolder();

    [GeneratedRegex(@"^(\D{0,4})(20\d{2})([-_. 年]?)(\d{1,2})(月?)$")]
    private static partial Regex YearMonthFolder();

    [GeneratedRegex(@"^(\d{1,2})(月)?$")]
    private static partial Regex MonthFolder();

    /// <summary>フォルダ直下が日付で整理されていれば、ファイル名（なければ今日）の日付に合う下位フォルダを返す。</summary>
    internal (string Path, bool Exists)? RefineByDate(string folder, TokenizedName name)
    {
        var now = _clock();
        int year = name.Year ?? now.Year;
        int month = name.Month ?? (name.Year == null ? now.Month : 0);
        // 候補自体が日付フォルダ（…\2026-09 など）なら、日付規則を持つ親から選び直す
        string current = Trim(folder);
        for (int up = 0; up < 2 && IsDateName(Leaf(current)) && Path.GetDirectoryName(current) is { Length: > 3 } parent; up++)
            current = parent;
        string start = current;

        for (int depth = 0; depth < 2; depth++)
        {
            var info = _source.GetFolder(current);
            if (info == null || info.SubfolderNames.Count == 0) break;
            string? next = DateChild(info.SubfolderNames, year, month);
            if (next == null) break;
            string path = Path.Combine(current, next);
            bool exists = info.SubfolderNames.Contains(next, StringComparer.OrdinalIgnoreCase);
            if (!exists) return (path, false);
            current = path;
        }
        // 日付フォルダへ1段も降りられなかった、または元の候補と同じなら補正なし
        if (string.Equals(current, start, StringComparison.OrdinalIgnoreCase) || string.Equals(current, Trim(folder), StringComparison.OrdinalIgnoreCase)) return null;
        return (current, true);
    }

    static bool IsDateName(string name) =>
        YearFolder().IsMatch(name) || YearMonthFolder().IsMatch(name) || MonthFolder().IsMatch(name);

    static string? DateChild(IReadOnlyList<string> subs, int year, int month)
    {
        // 過半数（かつ2件以上）が同じ日付規則なら、その規則で名前を作る
        string? Pick(Regex re, Func<Match, bool> valid, Func<Match, string?> make)
        {
            var matches = subs.Select(s => re.Match(s)).Where(m => m.Success && valid(m)).ToList();
            if (matches.Count < 2 || matches.Count * 2 < subs.Count) return null;
            return make(matches[^1]);
        }
        static bool IsMonth(string s) => int.TryParse(s, out int v) && v is >= 1 and <= 12;   // "00" などのキャッシュ名を月と誤認しない
        return Pick(YearMonthFolder(), m => IsMonth(m.Groups[4].Value), m => month <= 0 ? null :
                   m.Groups[1].Value + year + m.Groups[3].Value + (m.Groups[4].Value.Length == 2 ? month.ToString("00") : month.ToString()) + m.Groups[5].Value)
            ?? Pick(YearFolder(), _ => true, m => m.Groups[1].Value + year + m.Groups[3].Value)
            ?? Pick(MonthFolder(), m => IsMonth(m.Groups[1].Value), m => month <= 0 ? null : (m.Groups[1].Value.Length == 2 ? month.ToString("00") : month.ToString()) + m.Groups[2].Value);
    }

    // ================================================================ 新規フォルダ提案（§1.4）

    static readonly HashSet<string> NoFolderTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "msi", "msix", "appx", "zip", "7z", "rar", "iso", "ini", "lnk", "tmp", "dll", "torrent",
    };

    FolderSuggestion? ProposeNewFolder(TokenizedName name, List<Scored> scored, List<FolderSuggestion> top)
    {
        if (name.Words.Count == 0) return null;
        // 候補が弱い（手掛かりが乏しい）ときは、無関係な場所に作らないよう既定の置き場所の下に提案する
        var pool = scored.Take(5).Where(s => s.Score >= 0.25).ToList();
        if (pool.Count == 0 && NoFolderTypes.Contains(name.Ext)) return null;   // インストーラーや圧縮ファイルに分類フォルダは要らない

        // 候補のどのフォルダ名・ファイル名にも出てこない語が「新しい分類名」になる
        int bestIndex = -1;
        for (int i = 0; i < name.Words.Count; i++)
        {
            string w = name.Words[i];
            if (w.Length < 2 && !w.Any(char.IsLetter)) continue;
            if (w.All(char.IsAscii) && w.Length < 3) continue;
            bool inPath = pool.Any(s => FileNameTokenizer.Normalize(s.Info.Path).Contains(w, StringComparison.Ordinal));
            if (inPath) continue;
            int seen = pool.Count(s => s.Info.FileNames.Any(f => FileNameTokenizer.Normalize(f).Contains(w, StringComparison.Ordinal)));
            if (seen >= 1) continue;   // 既存フォルダのファイルに使われている語は、そこが置き場所（新しい分類ではない）
            if (bestIndex < 0 || name.Words[i].Length > name.Words[bestIndex].Length) bestIndex = i;
        }
        if (bestIndex < 0) return null;
        string newName = SanitizeFolderName(name.DisplayWords[bestIndex]);
        if (newName.Length == 0) return null;

        string? parent = pool.Count == 0 ? FallbackRoot : CommonParent(name, pool);
        if (parent == null || IsExcluded(parent)) return null;
        string path = Path.Combine(parent, newName);
        if (top.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) return null;
        if (_source.GetFolder(path) != null) return null;

        var conf = top.Count == 0 || top[0].Confidence == Confidence.Low ? Confidence.Medium : Confidence.Low;
        return new FolderSuggestion(path, 0, conf, $"「{Leaf(parent)}」に「{newName}」フォルダを作成", IsNew: true);
    }

    /// <summary>候補群を最も多く含む最下位の祖先。候補がまとまらなければ1位のフォルダ（名前が一致）かその親。</summary>
    string? CommonParent(TokenizedName name, List<Scored> pool)
    {
        var counts = new Dictionary<string, (int Count, int Depth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in pool)
        {
            string? cur = Trim(s.Info.Path);
            for (int up = 0; up < 3 && cur != null; up++)
            {
                int depth = cur.Count(ch => ch == '\\');
                if (cur.Length > 3 && !IsTooBroad(cur))
                {
                    var c = counts.GetValueOrDefault(cur);
                    counts[cur] = (c.Count + 1, depth);
                }
                cur = Path.GetDirectoryName(cur);
            }
        }
        var group = counts.Where(kv => kv.Value.Count >= 2 && !IsExcluded(kv.Key))
                          .OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Depth)
                          .Select(kv => kv.Key).FirstOrDefault();
        if (group != null) return group;

        string first = Trim(pool[0].Info.Path);
        if (FolderNameMatch(name, first, out _) >= 0.5) return first;
        return Path.GetDirectoryName(first) is { Length: > 3 } parent && !IsTooBroad(parent) ? parent : first;
    }

    /// <summary>ユーザーフォルダ直下など、新規フォルダの親として広すぎる場所。</summary>
    static bool IsTooBroad(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string p = Trim(path);
        return string.Equals(p, profile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, Path.GetDirectoryName(profile), StringComparison.OrdinalIgnoreCase);
    }

    static string SanitizeFolderName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Where(c => !invalid.Contains(c)).ToArray()).Trim(' ', '.');
    }

    static string Trim(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    static string Leaf(string path) => Path.GetFileName(Trim(path)) is { Length: > 0 } n ? n : path;
}
