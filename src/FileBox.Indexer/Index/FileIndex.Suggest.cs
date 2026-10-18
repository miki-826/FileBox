using System.Numerics;
using FileBox.Contracts;
using FileBox.Core.Search;
using FileBox.Core.Suggest;

namespace FileBox.Indexer.Index;

public sealed partial class FileIndex
{
    /// <summary>
    /// 保存先提案の候補フォルダを集める（02_機能設計 §1.3-2）。語を名前に含むファイルの親フォルダ（1点）と、
    /// 語を名前に含むフォルダ自身（2点）を数え、一致した語の種類が多いほど上位にする。
    /// </summary>
    public List<SuggestFolder> FindSuggestCandidates(IReadOnlyList<string> terms, int maxFolders = 60, int maxNames = 400, CancellationToken ct = default)
    {
        // 語はひらがな化済みなので、カタカナ表記の名前にも当たるよう両方で探す
        var variants = new List<(string Text, int Term)>();
        for (int t = 0; t < Math.Min(terms.Count, 16); t++)
        {
            variants.Add((terms[t], t));
            string kata = KanaConverter.HiraganaToKatakana(terms[t]);
            if (kata != terms[t]) variants.Add((kata, t));
        }
        var result = new List<SuggestFolder>();
        if (variants.Count == 0) return result;

        _lock.EnterReadLock();
        try
        {
            int n = _count;
            int chunks = (n + ChunkSize - 1) / ChunkSize;
            var partial = new Dictionary<int, (double Score, int Mask)>[chunks];
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, chunk =>
            {
                var local = new Dictionary<int, (double, int)>();
                int end = Math.Min(n, (chunk + 1) * ChunkSize);
                for (int i = chunk * ChunkSize; i < end; i++)
                {
                    var f = _flags[i];
                    if ((f & (EntryFlags.Deleted | EntryFlags.Root | EntryFlags.System)) != 0) continue;
                    string name = _names[i]!;
                    int mask = 0;
                    foreach (var (text, term) in variants)
                        if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) mask |= 1 << term;
                    if (mask == 0) continue;
                    bool isDir = (f & EntryFlags.Directory) != 0;
                    int folder = isDir ? i : _parents[i];
                    if (folder < 0) continue;
                    double w = (isDir ? 2.0 : 1.0) * BitOperations.PopCount((uint)mask);
                    var cur = local.GetValueOrDefault(folder);
                    local[folder] = (cur.Item1 + w, cur.Item2 | mask);
                }
                partial[chunk] = local;
            });

            var merged = new Dictionary<int, (double Score, int Mask)>();
            foreach (var part in partial)
                foreach (var (folder, (score, mask)) in part)
                {
                    var cur = merged.GetValueOrDefault(folder);
                    merged[folder] = (cur.Score + score, cur.Mask | mask);
                }

            // 一致した語の種類 × 件数（件数は対数で頭打ち）。システム系フォルダはここで除く
            var ranked = merged
                .Select(kv => (Id: kv.Key, Rank: (1 + BitOperations.PopCount((uint)kv.Value.Mask)) * Math.Log(2 + kv.Value.Score)))
                .OrderByDescending(x => x.Rank)
                .Take(maxFolders * 4);
            foreach (var (id, _) in ranked)
            {
                if (result.Count >= maxFolders) break;
                string path = GetPathCore(id);
                if (SuggestionEngine.IsExcluded(path)) continue;
                var folder = new SuggestFolder { Path = path };
                if (_children.TryGetValue(id, out var kids))
                {
                    foreach (int c in kids)
                    {
                        if ((_flags[c] & EntryFlags.Deleted) != 0) continue;
                        if ((_flags[c] & EntryFlags.Directory) != 0)
                        {
                            if (folder.Subfolders.Count < 200) folder.Subfolders.Add(_names[c]!);
                        }
                        else if (folder.Files.Count < maxNames) folder.Files.Add(_names[c]!);
                    }
                }
                EnsureMeta(id);
                folder.ModifiedUtc = _mtimes[id] > 0 ? new DateTime(_mtimes[id], DateTimeKind.Utc) : null;
                result.Add(folder);
            }
        }
        finally { _lock.ExitReadLock(); }
        return result;
    }
}
