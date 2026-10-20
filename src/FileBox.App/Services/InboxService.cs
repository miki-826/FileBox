using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;
using FileBox.Core.Suggest;
using FileBox.Data;
using Microsoft.UI.Dispatching;

namespace FileBox.App.Services;

/// <summary>受け取りボックスの1件。</summary>
public sealed partial class InboxItem : ObservableObject
{
    public required string FullPath { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public string SourceName => Path.GetFileName(Path.GetDirectoryName(FullPath) ?? "") ?? "";
    public long Size { get; init; }
    public DateTime Arrived { get; init; }
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
    public string Glyph => FileKinds.Glyph(Extension);
    public string Detail => $"{Format.HumanSize(Size)} · {Arrived:M/d HH:mm} · {SourceName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(ReasonText), nameof(HasTarget), nameof(ConfidenceText), nameof(Confidence))]
    SuggestionResult? _suggestion;

    /// <summary>ルールに一致した場合の移動先（提案より優先）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(ReasonText), nameof(HasTarget), nameof(ConfidenceText), nameof(Confidence))]
    InboxRule? _rule;

    [ObservableProperty]
    bool _isBusy = true;

    public string? TargetPath => Rule?.TargetPath ?? Suggestion?.Best?.Path;
    public bool TargetIsNew => Rule == null && Suggestion?.Candidates.Count == 0 && Suggestion.NewFolder != null;
    public bool HasTarget => TargetPath != null;
    public Confidence Confidence => Rule != null ? Confidence.High : Suggestion?.Best?.Confidence ?? Confidence.Low;
    public string ConfidenceText => Rule != null ? "ルール" : Suggestion?.Best?.ConfidenceText ?? "";
    public string TargetText => TargetPath is { } p ? (TargetIsNew ? "新規: " : "") + ShortPath(p) : IsBusy ? "提案を計算中…" : "候補なし";
    public string ReasonText => Rule != null ? "振り分けルールに一致" : Suggestion?.Best?.Reason ?? "";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(TargetText));

    static string ShortPath(string p)
    {
        var parts = p.TrimEnd('\\').Split('\\');
        return parts.Length <= 3 ? p : "…\\" + string.Join('\\', parts[^2..]);
    }
}

/// <summary>
/// 受け取りボックス（02_機能設計 §1.6）。ダウンロード・デスクトップ等を監視し、
/// 書込完了（5秒間サイズ不変）の新着に保存先を提案する。自動移動はルールで ON にしたものだけ。
/// </summary>
public sealed class InboxService
{
    static InboxService? _instance;
    public static InboxService Instance => _instance ?? throw new InvalidOperationException("InboxService.Initialize が未実行");

    static readonly TimeSpan StableFor = TimeSpan.FromSeconds(5);
    static readonly TimeSpan InitialWindow = TimeSpan.FromDays(14);
    static readonly string[] PartialExtensions = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    readonly UserStore _store;
    readonly DispatcherQueue _ui;
    readonly List<FileSystemWatcher> _watchers = new();
    readonly Dictionary<string, (long Size, DateTime Since)> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _suggestGate = new(1, 1);
    DispatcherQueueTimer? _timer;

    public ObservableCollection<InboxItem> Items { get; } = new();
    public event Action? CountChanged;

    InboxService(UserStore store, DispatcherQueue ui)
    {
        _store = store;
        _ui = ui;
        Items.CollectionChanged += (_, _) => CountChanged?.Invoke();
    }

    public static void Initialize(UserStore store, DispatcherQueue ui)
    {
        if (_instance != null) return;
        _instance = new InboxService(store, ui);
        _instance.Start();
    }

    public IReadOnlyList<string> Sources => _store.GetInboxSources();

    void Start()
    {
        if (_store.GetSetting("inbox.seeded") == null)
        {
            foreach (var path in new[] { ShellService.KnownFolder(ShellService.QuickAccess[2].Id) })
                if (path != null && Directory.Exists(path)) _store.AddInboxSource(path);
            _store.SetSetting("inbox.seeded", "1");
        }
        Restart();
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => CheckPending();
        _timer.Start();
    }

    /// <summary>監視フォルダを読み直す（設定変更後）。</summary>
    public void Restart()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        Items.Clear();
        var dismissed = _store.GetDismissedInboxItems();
        var initial = new List<InboxItem>();
        foreach (var source in Sources)
        {
            if (!Directory.Exists(source)) continue;
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (var f in new DirectoryInfo(source).EnumerateFiles("*", options))
                {
                    if (!IsCandidate(f.FullName) || dismissed.Contains(f.FullName)) continue;
                    if (DateTime.Now - f.CreationTime > InitialWindow) continue;
                    initial.Add(new InboxItem { FullPath = f.FullName, Size = f.Length, Arrived = f.CreationTime });
                }
                var watcher = new FileSystemWatcher(source)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => { _ui.TryEnqueue(() => Remove(e.OldFullPath)); Enqueue(e.FullPath); };
                watcher.Deleted += (_, e) => _ui.TryEnqueue(() => Remove(e.FullPath));
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
        foreach (var item in initial.OrderByDescending(i => i.Arrived).Take(200)) Items.Add(item);
        _ = SuggestWhenIndexReadyAsync(Items.ToList());
    }

    static bool IsCandidate(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith("~$") || name.StartsWith('.') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;
        string ext = Path.GetExtension(name);
        if (PartialExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    void Enqueue(string path)
    {
        if (!IsCandidate(path)) return;
        lock (_pending) _pending[path] = (-1, DateTime.Now);
    }

    /// <summary>サイズが5秒間変わらず、読み取りで開ければ「書込完了」とみなす。</summary>
    void CheckPending()
    {
        List<string> ready = new();
        lock (_pending)
        {
            foreach (var (path, state) in _pending.ToList())
            {
                FileInfo info;
                try { info = new FileInfo(path); } catch (Exception) { _pending.Remove(path); continue; }
                if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) { _pending.Remove(path); continue; }
                if (info.Length != state.Size) { _pending[path] = (info.Length, DateTime.Now); continue; }
                if (DateTime.Now - state.Since < StableFor) continue;
                try { using var _ = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { _pending.Remove(path); continue; }
                _pending.Remove(path);
                ready.Add(path);
            }
        }
        var added = new List<InboxItem>();
        foreach (var path in ready)
        {
            if (Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var info = new FileInfo(path);
            var item = new InboxItem { FullPath = path, Size = info.Length, Arrived = DateTime.Now };
            Items.Insert(0, item);
            added.Add(item);
        }
        if (added.Count > 0) _ = SuggestAllAsync(added, allowAutoMove: true);
    }

    /// <summary>
    /// 起動直後は索引の作成中で候補がほとんど出ないので、索引ができてから提案する（最大 2 分待ち、それ以上は今ある索引で）。
    /// </summary>
    async Task SuggestWhenIndexReadyAsync(List<InboxItem> items)
    {
        for (int i = 0; i < 120; i++)
        {
            var status = await IndexClient.Instance.GetStatusAsync();
            if (status?.State == Contracts.IndexState.Ready) break;
            await Task.Delay(1000);
        }
        await SuggestAllAsync(items, allowAutoMove: false);
    }

    async Task SuggestAllAsync(List<InboxItem> items, bool allowAutoMove)
    {
        var rules = _store.GetInboxRules();
        foreach (var item in items)
        {
            await _suggestGate.WaitAsync();
            try
            {
                if (!File.Exists(item.FullPath)) { Remove(item.FullPath); continue; }
                item.Rule = rules.FirstOrDefault(r => Matches(r, item.Name));
                if (item.Rule == null)
                {
                    try { item.Suggestion = await SuggestionService.Instance.SuggestAsync(item.FullPath); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
                item.IsBusy = false;
                CountChanged?.Invoke();
                if (allowAutoMove && item.Rule is { AutoMove: true } rule)
                    await MoveAsync(item, rule.TargetPath, auto: true);
            }
            finally { _suggestGate.Release(); }
        }
    }

    public static bool Matches(InboxRule rule, string fileName) => Core.Files.NameRule.Matches(rule.NamePattern, rule.Ext, fileName);

    // ================================================================ 操作

    public async Task MoveAsync(InboxItem item, string target, bool auto = false)
    {
        var shown = item.Suggestion;
        var moved = await Views.SuggestMoveDialog.MoveAndLearnAsync(new[] { item.FullPath }, target, shown);
        if (moved != null) Remove(item.FullPath);
        if (auto && moved != null) App.Window?.ShowInfo($"ルールにより「{item.Name}」を移動しました", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
            "元に戻す (Ctrl+Z)", () => _ = FileOperations.Instance.UndoAsync());
    }

    /// <summary>確信度「高」（ルール一致を含む）の項目をまとめて推奨先へ移動する。移動先ごとに 1 回の操作として記録される。</summary>
    public async Task<int> MoveAllConfidentAsync()
    {
        var targets = Items.Where(i => i.TargetPath != null && i.Confidence == Confidence.High && !i.TargetIsNew).ToList();
        var byTarget = targets.GroupBy(i => i.TargetPath!, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var group in byTarget)
        {
            var sources = group.Select(i => i.FullPath).ToList();
            var (op, _) = await FileOperations.Instance.MoveQuietAsync(sources, group.Key);
            foreach (var (from, _) in op?.Items ?? Array.Empty<(string, string)>())
            {
                var item = group.First(i => string.Equals(i.FullPath, from, StringComparison.OrdinalIgnoreCase));
                SuggestionService.Instance.Record(item.Name, group.Key, item.Suggestion);
                Remove(from);
                count++;
            }
        }
        return count;
    }

    public void Dismiss(InboxItem item)
    {
        _store.DismissInboxItem(item.FullPath);
        Remove(item.FullPath);
    }

    /// <summary>この項目と同じ拡張子（＋名前の先頭語）を常に target へ振り分けるルールを作る。</summary>
    public InboxRule AddRuleFrom(InboxItem item, string target, bool autoMove)
    {
        string ext = Path.GetExtension(item.Name).TrimStart('.').ToLowerInvariant();
        var words = item.Suggestion?.Name.DisplayWords;
        string? pattern = words is { Count: > 0 } ? $"*{words[0]}*" : null;
        _store.AddInboxRule(pattern, ext.Length > 0 ? ext : null, target, autoMove);
        return _store.GetInboxRules().Last();
    }

    public List<InboxRule> GetRules() => _store.GetInboxRules();
    public void AddRule(string? namePattern, string? ext, string target, bool autoMove) => _store.AddInboxRule(namePattern, ext, target, autoMove);
    public void DeleteRule(long id) => _store.DeleteInboxRule(id);
    public void SetRuleAutoMove(long id, bool on) => _store.SetInboxRuleAutoMove(id, on);

    public void AddSource(string path) { _store.AddInboxSource(path); Restart(); }
    public void RemoveSource(string path) { _store.RemoveInboxSource(path); Restart(); }

    public void Remove(string path)
    {
        var item = Items.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item != null) Items.Remove(item);
    }
}
