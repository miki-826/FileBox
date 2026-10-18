using System.Diagnostics;
using FileBox.Contracts;
using FileBox.Core.Search;

namespace FileBox.Indexer.Index;

[Flags]
public enum EntryFlags : byte
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,
    Deleted = 8,
    MetaMissing = 16,
    Root = 32,
}

/// <summary>一括投入・スナップショット用の生エントリ。Frn はNTFSファイル参照番号（走査方式では 0）。</summary>
public struct RawEntry
{
    public ulong Frn;
    public ulong ParentFrn;
    public string Name;
    public EntryFlags Flags;
    public long Size;
    public long MTimeUtcTicks;
}

/// <summary>
/// メモリ常駐の名前索引（02_機能設計 §3.2）。エントリは構造体配列（列指向）で保持し、
/// 検索は全件を並列に走査する。書き込みは <see cref="Write"/> のスコープ内でのみ行う。
/// </summary>
public sealed partial class FileIndex : IPathSource
{
    const int ChunkSize = 16384;

    string?[] _names = new string?[1024];
    int[] _parents = new int[1024];
    EntryFlags[] _flags = new EntryFlags[1024];
    long[] _sizes = new long[1024];
    long[] _mtimes = new long[1024];
    ulong[] _frns = new ulong[1024];
    byte[] _vols = new byte[1024];
    int _count;
    int _live;

    readonly Dictionary<int, List<int>> _children = new();
    readonly List<int> _roots = new();
    readonly Dictionary<ulong, int>?[] _frnMaps = new Dictionary<ulong, int>?[64];
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    public int LiveCount => Volatile.Read(ref _live);

    public WriteScope Write() => new(this);

    public int CountVolume(byte vol)
    {
        _lock.EnterReadLock();
        try
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
                if (_vols[i] == vol && (_flags[i] & EntryFlags.Deleted) == 0) n++;
            return n;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------- 読み取り

    public string GetPath(int id)
    {
        _lock.EnterReadLock();
        try { return GetPathCore(id); }
        finally { _lock.ExitReadLock(); }
    }

    string GetPathCore(int id)
    {
        Span<int> stack = stackalloc int[256];
        int depth = 0;
        int cur = id;
        while (cur >= 0 && depth < stack.Length)
        {
            stack[depth++] = cur;
            cur = _parents[cur];
        }
        if (depth == 1) return _names[id] + "\\";
        var sb = new System.Text.StringBuilder(128);
        for (int i = depth - 1; i >= 0; i--)
        {
            sb.Append(_names[stack[i]]);
            if (i > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    public int Resolve(string path)
    {
        _lock.EnterReadLock();
        try { return ResolveCore(path); }
        finally { _lock.ExitReadLock(); }
    }

    int ResolveCore(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return -1;
        int cur = -1;
        // ルート（"C:"）を探す
        foreach (int id in _roots)
        {
            if ((_flags[id] & EntryFlags.Deleted) == 0 && string.Equals(_names[id], parts[0], StringComparison.OrdinalIgnoreCase))
            {
                cur = id;
                break;
            }
        }
        if (cur < 0) return -1;
        for (int i = 1; i < parts.Length && cur >= 0; i++)
            cur = FindChildCore(cur, parts[i]);
        return cur;
    }

    int FindChildCore(int parent, string name)
    {
        if (!_children.TryGetValue(parent, out var list)) return -1;
        foreach (int c in list)
            if (string.Equals(_names[c], name, StringComparison.OrdinalIgnoreCase)) return c;
        return -1;
    }

    bool IsUnder(int id, int ancestor)
    {
        int cur = _parents[id];
        for (int guard = 0; cur >= 0 && guard < 512; guard++)
        {
            if (cur == ancestor) return true;
            cur = _parents[cur];
        }
        return false;
    }

    /// <summary>サイズ・更新日時が未取得なら実ファイルから取得する（MFT 列挙直後など）。</summary>
    void EnsureMeta(int id)
    {
        if ((_flags[id] & EntryFlags.MetaMissing) == 0) return;
        try
        {
            string path = GetPathCore(id);
            FileSystemInfo info = (_flags[id] & EntryFlags.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists)
            {
                _sizes[id] = info is FileInfo f ? f.Length : 0;
                _mtimes[id] = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception) { /* アクセス不可は 0 のまま */ }
        _flags[id] &= ~EntryFlags.MetaMissing;
    }

    public List<RawEntry> ExportVolume(byte vol)
    {
        _lock.EnterReadLock();
        try
        {
            var list = new List<RawEntry>(_count);
            for (int i = 0; i < _count; i++)
            {
                if (_vols[i] != vol || (_flags[i] & (EntryFlags.Deleted | EntryFlags.Root)) != 0) continue;
                int p = _parents[i];
                list.Add(new RawEntry
                {
                    Frn = _frns[i],
                    ParentFrn = p >= 0 ? _frns[p] : 0,
                    Name = _names[i]!,
                    Flags = _flags[i],
                    Size = _sizes[i],
                    MTimeUtcTicks = _mtimes[i],
                });
            }
            return list;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>指定ボリュームのディレクトリで、メタ情報未取得の子を持つものを列挙する。</summary>
    public List<(int Id, string Path)> DirectoriesNeedingMeta(byte vol)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<(int, string)>();
            foreach (var (dir, kids) in _children)
            {
                if (_vols[dir] != vol || (_flags[dir] & EntryFlags.Deleted) != 0) continue;
                foreach (int c in kids)
                {
                    if ((_flags[c] & EntryFlags.MetaMissing) != 0)
                    {
                        result.Add((dir, GetPathCore(dir)));
                        break;
                    }
                }
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------- 検索

    public SearchResult Search(CompiledQuery query, int limit, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new SearchResult();
        _lock.EnterReadLock();
        try
        {
            int scope = -1;
            if (query.Source.InPath is { } inPath)
            {
                scope = ResolveCore(inPath);
                if (scope < 0)
                {
                    result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }
            }

            int n = _count;
            int chunks = (n + ChunkSize - 1) / ChunkSize;
            var buckets = new List<int>[chunks];
            var options = new ParallelOptions { CancellationToken = ct };
            Parallel.For(0, chunks, options, chunk =>
            {
                var local = new List<int>();
                int end = Math.Min(n, (chunk + 1) * ChunkSize);
                for (int i = chunk * ChunkSize; i < end; i++)
                {
                    var f = _flags[i];
                    if ((f & (EntryFlags.Deleted | EntryFlags.Root)) != 0) continue;
                    if (!query.Match(_names[i]!, (f & EntryFlags.Directory) != 0, this, i)) continue;
                    if (scope >= 0 && !IsUnder(i, scope)) continue;
                    if (query.NeedsMetadata)
                    {
                        EnsureMeta(i);
                        if (!query.MatchMetadata(_sizes[i], LocalTime(_mtimes[i]))) continue;
                    }
                    local.Add(i);
                }
                buckets[chunk] = local;
            });

            int total = 0;
            foreach (var b in buckets) total += b.Count;
            result.Total = total;

            // 上位 limit 件だけを選ぶ（完全一致 > 前方一致 > 名前が短い > 名前順）
            var top = SelectTop(buckets, total, limit, query);
            result.Truncated = total > top.Count;
            foreach (int id in top)
            {
                EnsureMeta(id);
                bool isDir = (_flags[id] & EntryFlags.Directory) != 0;
                result.Items.Add(new SearchItem
                {
                    Name = _names[id]!,
                    Path = GetPathCore(id),
                    IsDirectory = isDir,
                    Size = _sizes[id],
                    Modified = LocalTime(_mtimes[id]),
                });
            }
        }
        finally { _lock.ExitReadLock(); }
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    List<int> SelectTop(List<int>[] buckets, int total, int limit, CompiledQuery query)
    {
        var comparer = Comparer<(int Rank, int Id)>.Create((a, b) =>
        {
            int c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = _names[a.Id]!.Length.CompareTo(_names[b.Id]!.Length);
            if (c != 0) return c;
            return string.Compare(_names[a.Id], _names[b.Id], StringComparison.OrdinalIgnoreCase);
        });

        if (total <= limit)
        {
            var all = new List<(int, int)>(total);
            foreach (var b in buckets)
                foreach (int id in b) all.Add((query.Rank(_names[id]!), id));
            all.Sort(comparer);
            return all.Select(x => x.Item2).ToList();
        }

        // 最大ヒープで上位 limit 件を保持
        var heap = new PriorityQueue<(int Rank, int Id), (int Rank, int Id)>(Comparer<(int Rank, int Id)>.Create((a, b) => comparer.Compare(b, a)));
        foreach (var b in buckets)
        {
            foreach (int id in b)
            {
                var key = (query.Rank(_names[id]!), id);
                if (heap.Count < limit) heap.Enqueue(key, key);
                else if (comparer.Compare(key, heap.Peek()) < 0) heap.EnqueueDequeue(key, key);
            }
        }
        var list = new List<(int Rank, int Id)>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Sort(comparer);
        return list.Select(x => x.Id).ToList();
    }

    static DateTime? LocalTime(long utcTicks) =>
        utcTicks <= 0 ? null : new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
}
