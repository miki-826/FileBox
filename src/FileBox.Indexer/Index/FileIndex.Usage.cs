using FileBox.Contracts;

namespace FileBox.Indexer.Index;

public sealed partial class FileIndex
{
    /// <summary>
    /// path 配下のファイルサイズ合計（02_機能設計 §5.1 の容量集計）。索引に無い場所は -1。
    /// MFT 列挙直後でサイズ未取得の項目は 0 として数える（MetadataFiller が埋めるにつれ正確になる）。
    /// </summary>
    public long SubtreeSize(string path)
    {
        _lock.EnterReadLock();
        try
        {
            int root = ResolveCore(path);
            return root < 0 ? -1 : SumCore(root, null, 0, out _, out _, out _);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>容量分析ビュー用: 直下の項目ごとの合計と、配下で大きいファイル上位 top 件。</summary>
    public UsageReport? Usage(string path, int top = 100)
    {
        _lock.EnterReadLock();
        try
        {
            int root = ResolveCore(path);
            if (root < 0) return null;
            var report = new UsageReport { Path = GetPathCore(root) };
            var largest = new PriorityQueue<int, long>();   // 最小ヒープで上位を保つ
            if (_children.TryGetValue(root, out var kids))
            {
                foreach (int c in kids)
                {
                    if ((_flags[c] & EntryFlags.Deleted) != 0) continue;
                    bool dir = (_flags[c] & EntryFlags.Directory) != 0;
                    long files = 1, folders = 0;
                    bool partial = (_flags[c] & EntryFlags.MetaMissing) != 0;
                    long size = dir ? SumCore(c, largest, top, out files, out folders, out partial) : _sizes[c];
                    if (!dir) Offer(largest, c, top);
                    report.Children.Add(new UsageItem { Path = GetPathCore(c), IsDirectory = dir, Size = size, Modified = LocalTime(_mtimes[c]), IsPartial = partial });
                    report.Total += size;
                    report.FileCount += files;
                    report.FolderCount += folders + (dir ? 1 : 0);
                    report.IsPartial |= partial;
                }
            }
            report.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            var list = new List<int>();
            while (largest.Count > 0) list.Add(largest.Dequeue());
            list.Reverse();
            report.Largest = list.Select(id => new UsageItem { Path = GetPathCore(id), Size = _sizes[id], Modified = LocalTime(_mtimes[id]), IsPartial = (_flags[id] & EntryFlags.MetaMissing) != 0 }).ToList();
            return report;
        }
        finally { _lock.ExitReadLock(); }
    }

    long SumCore(int root, PriorityQueue<int, long>? largest, int top, out long files, out long folders, out bool partial)
    {
        files = folders = 0;
        partial = false;
        long total = 0;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            if (!_children.TryGetValue(cur, out var kids)) continue;
            foreach (int c in kids)
            {
                var f = _flags[c];
                if ((f & EntryFlags.Deleted) != 0) continue;
                if ((f & EntryFlags.Directory) != 0) { folders++; stack.Push(c); }
                else
                {
                    files++;
                    partial |= (f & EntryFlags.MetaMissing) != 0;
                    total += _sizes[c];
                    if (largest != null) Offer(largest, c, top);
                }
            }
        }
        return total;
    }

    void Offer(PriorityQueue<int, long> heap, int id, int top)
    {
        if (top <= 0) return;
        if (heap.Count < top) heap.Enqueue(id, _sizes[id]);
        else if (heap.TryPeek(out _, out long min) && _sizes[id] > min) heap.EnqueueDequeue(id, _sizes[id]);
    }

    /// <summary>パスが索引にあればそのサイズ（ファイル）。無ければ null。</summary>
    public long? SizeOf(string path)
    {
        _lock.EnterReadLock();
        try
        {
            int id = ResolveCore(path);
            if (id < 0) return null;
            EnsureMeta(id);
            return _sizes[id];
        }
        finally { _lock.ExitReadLock(); }
    }
}
