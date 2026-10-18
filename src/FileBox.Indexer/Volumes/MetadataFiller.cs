using System.IO.Enumeration;
using FileBox.Indexer.Index;

namespace FileBox.Indexer.Volumes;

/// <summary>
/// MFT 列挙では取得できないサイズ・更新日時を、ディレクトリ単位でバックグラウンド取得する（02_機能設計 §3.1）。
/// 未取得のエントリは検索時に個別取得されるため、これは応答性向上のための先読み。
/// </summary>
internal static class MetadataFiller
{
    readonly record struct Meta(string Name, long Size, long MTime);

    public static void Fill(FileIndex index, byte vol, CancellationToken ct)
    {
        var dirs = index.DirectoriesNeedingMeta(vol);
        Log.Info($"メタ情報の取得開始: vol={vol} dirs={dirs.Count}");
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        Parallel.ForEach(dirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, dir =>
        {
            List<Meta> metas;
            try
            {
                metas = new FileSystemEnumerable<Meta>(dir.Path,
                    (ref FileSystemEntry e) => new Meta(e.FileName.ToString(), e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks),
                    options).ToList();
            }
            catch (Exception) { return; }
            if (metas.Count == 0) return;

            using var w = index.Write();
            if (!w.IsLive(dir.Id)) return;
            var kids = w.Children(dir.Id);
            Dictionary<string, int>? byName = null;
            if (kids.Count > 32)
            {
                byName = new Dictionary<string, int>(kids.Count, StringComparer.OrdinalIgnoreCase);
                foreach (int k in kids) byName.TryAdd(w.Name(k), k);
            }
            foreach (var m in metas)
            {
                int child = -1;
                if (byName != null) byName.TryGetValue(m.Name, out child);
                else
                {
                    foreach (int k in kids)
                        if (string.Equals(w.Name(k), m.Name, StringComparison.OrdinalIgnoreCase)) { child = k; break; }
                }
                if (child >= 0 && w.HasMetaMissing(child)) w.SetMeta(child, m.Size, m.MTime);
            }
        });
        Log.Info($"メタ情報の取得完了: vol={vol}");
    }
}
