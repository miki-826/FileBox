using FileBox.Core.Suggest;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// 保存先提案（02_機能設計 §1）の App 側。候補は Indexer の名前索引から集め、
/// 繋がらないときはユーザーフォルダを浅く走査して代用する。学習は user.db の save_history。
/// </summary>
public sealed class SuggestionService : IFolderSource
{
    static SuggestionService? _instance;
    public static SuggestionService Instance => _instance ?? throw new InvalidOperationException("SuggestionService.Initialize が未実行");

    readonly UserStore _store;
    readonly SuggestionEngine _engine;

    SuggestionService(UserStore store)
    {
        _store = store;
        _engine = new SuggestionEngine(this, LoadHistory)
        {
            FallbackRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            PreferredRoots = PreferredRoots(),
            ExcludedFolders = store.GetSuggestExcluded(),
        };
    }

    public static void Initialize(UserStore store) => _instance ??= new SuggestionService(store);

    /// <summary>「候補に出さない場所」を変えたあとに読み直す。</summary>
    public void ReloadExclusions() => _engine.ExcludedFolders = _store.GetSuggestExcluded();

    /// <summary>利用者のデータ置き場: ユーザーフォルダと、システム以外のドライブ（データ用・クラウド同期）。</summary>
    static List<string> PreferredRoots()
    {
        var roots = new[]
        {
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        }.Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToList();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        try
        {
            roots.AddRange(DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable
                            && !string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name));
        }
        catch (Exception) { }
        return roots;
    }

    public Task<SuggestionResult> SuggestAsync(string filePath, CancellationToken ct = default) =>
        Task.Run(() => _engine.SuggestAsync(Path.GetFileName(filePath), Path.GetDirectoryName(filePath), ct), ct);

    /// <summary>確定した保存先を学習する。提案1位以外を選んだら、その1位を「選ばれなかった」として記録。</summary>
    public void Record(string fileName, string chosenFolder, SuggestionResult? shown)
    {
        var name = shown?.Name ?? FileNameTokenizer.Tokenize(fileName);
        if (name.Words.Count == 0 && name.Ext.Length == 0) return;
        int? rank = null;
        string? rejected = null;
        if (shown != null)
        {
            int i = shown.Candidates.ToList().FindIndex(c => SamePath(c.Path, chosenFolder));
            if (i >= 0) rank = i + 1;
            else if (shown.NewFolder is { } nf && SamePath(nf.Path, chosenFolder)) rank = 0;
            if (shown.Candidates.Count > 0 && !SamePath(shown.Candidates[0].Path, chosenFolder)) rejected = shown.Candidates[0].Path;
        }
        try { _store.AddSaveHistory(name.Key, name.Ext.Length > 0 ? name.Ext : null, chosenFolder, rank, rejected); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    IReadOnlyList<SaveRecord> LoadHistory() =>
        _store.GetSaveHistory()
              .Select(h => new SaveRecord(h.Tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries), h.Ext, h.FolderPath, h.RejectedPath, h.ChosenAt))
              .ToList();

    // ================================================================ IFolderSource

    public async Task<IReadOnlyList<FolderInfo>> FindCandidatesAsync(IReadOnlyList<string> terms, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var folders = await IndexClient.Instance.SuggestAsync(terms, timeout.Token).ConfigureAwait(false);
            if (folders != null)
                return folders.Select(f => new FolderInfo(f.Path, f.Files, f.Subfolders, f.ModifiedUtc)).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        return ScanUserFolders(terms, ct);
    }

    /// <summary>Indexer が使えないときの代替: ユーザーフォルダを3階層まで見て、語を含むものを候補にする。</summary>
    List<FolderInfo> ScanUserFolders(IReadOnlyList<string> terms, CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        }.Select(Environment.GetFolderPath).Where(Directory.Exists);

        var result = new List<FolderInfo>();
        var queue = new Queue<(string Path, int Depth)>(roots.Select(r => (r, 0)));
        int visited = 0;
        while (queue.Count > 0 && visited < 3000 && result.Count < 60)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            visited++;
            if (GetFolder(dir) is not { } info) continue;
            bool hit = terms.Any(t => FileNameTokenizer.Normalize(Path.GetFileName(dir)).Contains(t))
                    || info.FileNames.Any(f => terms.Any(t => FileNameTokenizer.Normalize(f).Contains(t)));
            if (hit) result.Add(info);
            if (depth < 3)
                foreach (var sub in info.SubfolderNames) queue.Enqueue((Path.Combine(dir, sub), depth + 1));
        }
        return result;
    }

    public FolderInfo? GetFolder(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return null;
            var files = new List<string>();
            var subs = new List<string>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var e in dir.EnumerateFileSystemInfos("*", options))
            {
                if (e is DirectoryInfo) { if (subs.Count < 200) subs.Add(e.Name); }
                else if (files.Count < 400) files.Add(e.Name);
                if (subs.Count >= 200 && files.Count >= 400) break;
            }
            return new FolderInfo(dir.FullName, files, subs, dir.LastWriteTimeUtc);
        }
        catch (Exception) { return null; }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
