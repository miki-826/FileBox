using System.Runtime.InteropServices;
using System.Text;
using FileBox.Core.Files;
using FileBox.Data;
using Microsoft.UI.Dispatching;

namespace FileBox.App.Services;

/// <summary>
/// ファイルの中身を検索するための索引づくり（既定はオフ。設定で指定したフォルダーだけ）。
/// 利用者の権限で動くので、その人が読めないファイルは索引に入らない。テキスト・コード・Office は Core の TextExtractor、
/// PDF などは Windows に入っている IFilter があればそれで読む。変更は FileSystemWatcher で追い、起動時に差分だけ読み直す。
/// </summary>
public sealed class ContentIndexer : IDisposable
{
    public static ContentIndexer Instance { get; } = new();

    UserStore? _store;
    ContentStore? _content;
    readonly List<FileSystemWatcher> _watchers = new();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _cts;
    DispatcherQueueTimer? _timer;

    /// <summary>進み具合（索引に入れた件数・調べる残り）。設定画面に表示する。</summary>
    public string Status { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public event Action? StatusChanged;

    public bool Enabled => _store?.GetSetting("content.enabled") == "1";
    public IReadOnlyList<string> Roots => _store?.GetContentRoots() ?? new();

    public void Initialize(UserStore store, DispatcherQueue ui)
    {
        _store = store;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => FlushPending();
        if (Enabled) Start();
    }

    public void SetEnabled(bool on)
    {
        _store?.SetSetting("content.enabled", on ? "1" : "0");
        if (on) Start(); else Stop();
    }

    public void AddRoot(string path)
    {
        _store?.SetContentRoot(path, true);
        if (Enabled) Start();
    }

    public void RemoveRoot(string path)
    {
        _store?.SetContentRoot(path, false);
        Store().RemoveUnder(path);
        if (Enabled) Start();
    }

    ContentStore Store() => _content ??= new ContentStore();

    /// <summary>本文に語を含むファイル（オフのときは空）。</summary>
    public List<ContentHit> Search(string text, int limit = 200) => Enabled ? Store().Search(text, limit) : new();

    void Start()
    {
        Stop();
        var roots = Roots.Where(Directory.Exists).ToList();
        foreach (var root in roots)
        {
            try
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                w.Created += (_, e) => Queue(e.FullPath);
                w.Changed += (_, e) => Queue(e.FullPath);
                w.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                w.Deleted += (_, e) => Queue(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { AppLog.Error($"本文の索引の監視を始められませんでした: {root}", ex); }
        }
        _timer?.Start();
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => Crawl(roots, cts.Token));
    }

    void Stop()
    {
        _cts?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _timer?.Stop();
        lock (_pending) _pending.Clear();
        IsRunning = false;
        SetStatus(Enabled ? "" : "オフ");
    }

    /// <summary>止める（設定画面の「中止」）。次に起動したとき、またはオンにし直したときに続きから読む。</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        SetStatus("中止しました（次の起動で続きから読みます）");
    }

    void Queue(string path)
    {
        lock (_pending) _pending.Add(path);
    }

    void FlushPending()
    {
        List<string> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToList();
            _pending.Clear();
        }
        _ = Task.Run(() =>
        {
            foreach (var path in batch)
            {
                if (File.Exists(path)) IndexFile(new FileInfo(path));
                else Store().Remove(path);
            }
        });
    }

    void Crawl(IReadOnlyList<string> roots, CancellationToken ct)
    {
        IsRunning = true;
        int done = 0, skipped = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000 | FileAttributes.System };
            foreach (var root in roots)
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!CanRead(file.Extension)) continue;
                    if (Store().IsCurrent(file.FullName, file.Length, file.LastWriteTimeUtc)) { skipped++; continue; }
                    if (IndexFile(file)) done++;
                    if (done % 25 == 0) SetStatus($"本文を読み込み中… {done:N0} 件（変更なし {skipped:N0} 件）");
                }
            }
            int pruned = Store().Prune(File.Exists);
            SetStatus($"索引 {Store().Count:N0} 件（今回読み込み {done:N0} 件・削除 {pruned:N0} 件）");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("本文の索引づくりに失敗しました", ex);
            SetStatus("エラーで止まりました（ログに記録しました）");
        }
        finally { IsRunning = false; }
    }

    static bool CanRead(string ext) => TextExtractor.Supports(ext) || IFilterReader.Supports(ext);

    bool IndexFile(FileInfo file)
    {
        try
        {
            if (!CanRead(file.Extension) || file.Length > TextExtractor.MaxFileBytes) return false;
            string? text = TextExtractor.Supports(file.Extension) ? TextExtractor.Extract(file.FullName) : IFilterReader.Read(file.FullName, TextExtractor.MaxChars);
            if (text == null) return false;
            Store().Upsert(file.FullName, file.Length, file.LastWriteTimeUtc, text);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"本文を読めませんでした: {file.FullName}", ex);
            return false;
        }
    }

    void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _content?.Dispose();
    }
}

/// <summary>
/// Windows の IFilter（Windows Search と同じ仕組み）で本文を読む。PDF などは、対応するフィルターが入っているときだけ読める。
/// </summary>
static class IFilterReader
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".rtf", ".doc", ".xls", ".ppt", ".msg", ".eml" };

    public static bool Supports(string ext) => Extensions.Contains(ext);

    public static string? Read(string path, int maxChars)
    {
        IFilter? filter = null;
        try
        {
            if (LoadIFilter(path, IntPtr.Zero, out var unknown) != 0 || unknown == null) return null;
            filter = (IFilter)unknown;
            const uint flags = 1 | 2;   // IFILTER_INIT_CANON_PARAGRAPHS | IFILTER_INIT_HARD_LINE_BREAKS
            if (filter.Init(flags, 0, IntPtr.Zero, out _) != 0) return null;
            var sb = new StringBuilder();
            var buffer = new char[4096];
            while (sb.Length < maxChars)
            {
                int hr = filter.GetChunk(out var chunk);
                if (hr != 0) break;   // FILTER_E_END_OF_CHUNKS など
                if ((chunk.flags & 1) == 0) continue;   // CHUNK_TEXT 以外
                while (sb.Length < maxChars)
                {
                    uint size = (uint)buffer.Length;
                    hr = filter.GetText(ref size, buffer);
                    if (size > 0) sb.Append(buffer, 0, (int)size);
                    if (hr != 0) break;   // FILTER_E_NO_MORE_TEXT など
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception) { return null; }
        finally { if (filter != null) Marshal.ReleaseComObject(filter); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STAT_CHUNK
    {
        public uint idChunk;
        public int breakType;
        public int flags;
        public uint locale;
        public Guid attributeGuid;
        public int attributeKind;
        public IntPtr attributeValue;
        public uint idChunkSource;
        public uint cwcStartSource;
        public uint cwcLenSource;
    }

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFilter
    {
        [PreserveSig] int Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pFlags);
        [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
        [PreserveSig] int GetText(ref uint pcwcBuffer, [Out, MarshalAs(UnmanagedType.LPArray)] char[] awcBuffer);
        [PreserveSig] int GetValue(out IntPtr ppPropValue);
        [PreserveSig] int BindRegion(IntPtr origPos, ref Guid riid, out IntPtr ppunk);
    }

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, [MarshalAs(UnmanagedType.IUnknown)] out object? ppIUnk);
}
