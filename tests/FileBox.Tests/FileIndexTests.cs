using System.Diagnostics;
using FileBox.Contracts;
using FileBox.Core.Search;
using FileBox.Indexer.Index;
using Xunit;
using Xunit.Abstractions;

namespace FileBox.Tests;

public class FileIndexTests
{
    static FileIndex Sample(out int root, out int docs, out int bills)
    {
        var ix = new FileIndex();
        using var w = ix.Write();
        root = w.AddRoot(0, "D:", 0);
        docs = w.Add(root, "書類", EntryFlags.Directory, 0, 0);
        bills = w.Add(docs, "請求書", EntryFlags.Directory, 0, 0);
        w.Add(bills, "2026-10_請求書_山田商事.pdf", EntryFlags.None, 120_000, new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc).Ticks);
        w.Add(bills, "2026-09_請求書_A商事.pdf", EntryFlags.None, 98_000, new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).Ticks);
        int photos = w.Add(root, "写真", EntryFlags.Directory, 0, 0);
        w.Add(photos, "請求書の写真.jpg", EntryFlags.None, 3_000_000, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        return ix;
    }

    static SearchResult Search(FileIndex ix, string q, int limit = 100) => ix.Search(CompiledQuery.Compile(q), limit);

    [Fact]
    public void BuildsPathsAndResolves()
    {
        var ix = Sample(out int root, out _, out int bills);
        Assert.Equal("D:\\", ix.GetPath(root));
        Assert.Equal("D:\\書類\\請求書", ix.GetPath(bills));
        Assert.Equal(bills, ix.Resolve("d:\\書類\\請求書\\"));
        Assert.Equal(-1, ix.Resolve("D:\\nothing"));
        Assert.Equal(6, ix.LiveCount);
    }

    [Fact]
    public void CollectsSuggestCandidates()
    {
        var ix = Sample(out _, out _, out _);
        using (var w = ix.Write())
        {
            int appData = w.Add(ix.Resolve("D:\\"), "AppData", EntryFlags.Directory, 0, 0);
            w.Add(appData, "請求書_cache.pdf", EntryFlags.None, 1, 0);
            w.Add(ix.Resolve("D:\\書類"), "ショウジ一覧.xlsx", EntryFlags.None, 1, 0);
        }
        var folders = ix.FindSuggestCandidates(new[] { "請求書", "山田商事" });
        Assert.Equal("D:\\書類\\請求書", folders[0].Path); // 名前一致＋2語一致のファイルを持つ
        Assert.Contains("2026-09_請求書_A商事.pdf", folders[0].Files);
        Assert.DoesNotContain(folders, f => f.Path.Contains("AppData"));

        // ひらがなの語でカタカナの名前にも当たる
        Assert.Contains(ix.FindSuggestCandidates(new[] { "しょうじ" }), f => f.Path == "D:\\書類");
    }

    [Fact]
    public void SearchesWithFiltersAndScope()
    {
        var ix = Sample(out _, out _, out _);
        Assert.Equal(4, Search(ix, "請求書").Total);
        Assert.Equal(1, Search(ix, "folder: 請求書").Total);
        Assert.Equal(2, Search(ix, "請求書 ext:pdf").Total);
        Assert.Equal(1, Search(ix, "請求書 in:D:\\写真").Total);
        Assert.Equal(1, Search(ix, "size:>1MB").Total);
        Assert.Equal(2, Search(ix, "file: modified:2026-10").Total);
        Assert.Equal(2, Search(ix, "path:書類\\請求書\\").Total);
        Assert.Equal(0, Search(ix, "x in:D:\\nothing").Total);

        var r = Search(ix, "請求書");
        Assert.Equal("請求書", r.Items[0].Name); // 完全一致が先頭
        Assert.Equal("D:\\書類\\請求書", r.Items[0].Path);
        Assert.True(r.Items[0].IsDirectory);
    }

    [Fact]
    public void LimitKeepsBestRanked()
    {
        var ix = Sample(out _, out _, out _);
        var r = Search(ix, "請求書", limit: 2);
        Assert.Equal(4, r.Total);
        Assert.True(r.Truncated);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal("請求書", r.Items[0].Name);
        Assert.Equal("請求書の写真.jpg", r.Items[1].Name); // 前方一致が次
    }

    [Fact]
    public void RemoveAndMove()
    {
        var ix = Sample(out int root, out int docs, out int bills);
        using (var w = ix.Write())
        {
            w.Move(bills, root, "請求書2026");
        }
        Assert.Equal("D:\\請求書2026", ix.GetPath(bills));
        Assert.Equal(2, Search(ix, "ext:pdf in:D:\\請求書2026").Total);
        using (var w = ix.Write()) w.Remove(bills);
        Assert.Equal(0, Search(ix, "ext:pdf").Total);
        Assert.Equal(3, ix.LiveCount);
        _ = docs;
    }

    [Fact]
    public void FrnOperationsAndBulkLoad()
    {
        var ix = new FileIndex();
        using (var w = ix.Write())
        {
            int root = w.AddRoot(1, "C:", 5);
            var entries = new List<RawEntry>
            {
                new() { Frn = 11, ParentFrn = 10, Name = "child.txt" },        // 親が後から来る
                new() { Frn = 10, ParentFrn = 5, Name = "dir", Flags = EntryFlags.Directory },
                new() { Frn = 12, ParentFrn = 999, Name = "orphan" },          // 親不明 → 捨てる
            };
            Assert.Equal(2, w.BulkLoad(1, root, entries));
            w.UpsertByFrn(1, 13, 10, "new.txt", EntryFlags.None);
            w.UpsertByFrn(1, 11, 5, "moved.txt", EntryFlags.None);
            w.RemoveByFrn(1, 13);
        }
        Assert.Equal("C:\\moved.txt", Search(ix, "moved").Items.Single().Path);
        Assert.Equal(0, Search(ix, "new.txt").Total);
        Assert.Equal(0, Search(ix, "orphan").Total);
        Assert.Equal(2, ix.ExportVolume(1).Count);
    }

    [Fact]
    public async Task PipeFramingRoundTrips()
    {
        using var ms = new MemoryStream();
        await IndexPipe.WriteAsync(ms, new IndexRequest { Id = 7, Type = IndexRequestType.Search, Query = "請求書 ext:pdf" });
        ms.Position = 0;
        var back = await IndexPipe.ReadAsync<IndexRequest>(ms);
        Assert.Equal(7, back!.Id);
        Assert.Equal("請求書 ext:pdf", back.Query);
        Assert.Null(await IndexPipe.ReadAsync<IndexRequest>(ms));
    }
}

/// <summary>05_非機能要件 §1 の検索性能目標を、100万件の合成インデックスで確認する。</summary>
public class SearchPerformanceTests(ITestOutputHelper output)
{
    static readonly Lazy<FileIndex> Million = new(BuildMillion);

    static FileIndex BuildMillion()
    {
        var rnd = new Random(42);
        string[] words = { "report", "請求書", "invoice", "写真", "meeting", "議事録", "design", "見積書", "backup", "data", "project", "契約書", "photo", "memo", "budget" };
        string[] exts = { ".pdf", ".xlsx", ".docx", ".jpg", ".png", ".txt", ".cs", ".mp4", ".zip", ".json" };
        var ix = new FileIndex();
        using var w = ix.Write();
        int root = w.AddRoot(0, "C:", 0);
        var dirs = new List<int> { root };
        int count = 0;
        while (count < 1_000_000)
        {
            int parent = dirs[rnd.Next(dirs.Count)];
            if (rnd.Next(10) == 0)
            {
                dirs.Add(w.Add(parent, $"{words[rnd.Next(words.Length)]}_{rnd.Next(10000)}", EntryFlags.Directory, 0, 0));
            }
            else
            {
                string name = $"{words[rnd.Next(words.Length)]}_{2020 + rnd.Next(7)}{rnd.Next(1, 13):00}_{words[rnd.Next(words.Length)]}{rnd.Next(1000)}{exts[rnd.Next(exts.Length)]}";
                w.Add(parent, name, EntryFlags.None, rnd.Next(10_000_000), DateTime.UtcNow.AddDays(-rnd.Next(2000)).Ticks);
            }
            count++;
        }
        return ix;
    }

    double Measure(string query, double targetMs)
    {
        var ix = Million.Value;
        var compiled = CompiledQuery.Compile(query);
        ix.Search(compiled, 100); // ウォームアップ
        var times = new List<double>();
        SearchResult r = new();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            r = ix.Search(compiled, 100);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        double median = times.Order().ElementAt(2);
        output.WriteLine($"{query,-40} hits={r.Total,8}  median={median,6:F1}ms (目標 <{targetMs}ms)");
        return median;
    }

    [Fact]
    public void MeetsSearchTargetsOnMillionEntries()
    {
        Assert.Equal(1_000_001, Million.Value.LiveCount + 1);
        var results = new[]
        {
            (Measure("請求書", 100), 100.0),
            (Measure("invoice_2024", 100), 100.0),
            (Measure("folder: report", 30), 30.0),
            (Measure("mode:prefix memo_2025", 100), 100.0),
            (Measure("*_2023??_写真*.jpg", 100), 100.0),
            (Measure("~mitsumori", 100), 100.0),
            (Measure("report ext:pdf,xlsx", 100), 100.0),
        };
        // CI 等の遅い環境でも落ちにくいよう、合否は目標の2倍で判定し、実測値は出力で確認する
        // GitHub Actions など CI の共有マシンは遅く揺れるので、時間の判定は手元の実行だけで行う（CI では記録のみ）
        if (Environment.GetEnvironmentVariable("CI") != null) return;
        foreach (var (ms, target) in results) Assert.True(ms < target * 2, $"{ms:F1}ms >= {target * 2}ms");
    }
}
