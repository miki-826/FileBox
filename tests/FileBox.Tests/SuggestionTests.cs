using FileBox.Core.Suggest;
using Xunit;

namespace FileBox.Tests;

public class FileNameTokenizerTests
{
    [Fact]
    public void SplitsJapaneseNameAndExtractsDate()
    {
        var t = FileNameTokenizer.Tokenize("2026年10月_請求書_山田商事.pdf");
        Assert.Equal(2026, t.Year);
        Assert.Equal(10, t.Month);
        Assert.Equal("pdf", t.Ext);
        Assert.Contains("請求書", t.Words);
        Assert.Contains("山田商事", t.Words);
        Assert.Contains("請求", t.Grams);
    }

    [Theory]
    [InlineData("invoice_20261018.pdf", 2026, 10)]
    [InlineData("報告書 2025-03.docx", 2025, 3)]
    [InlineData("議事録_R8_4月.docx", 2026, 4)]
    [InlineData("令和7年度 予算.xlsx", 2025, null)]
    [InlineData("error2.log", null, null)]
    public void DetectsDates(string name, int? year, int? month)
    {
        var t = FileNameTokenizer.Tokenize(name);
        Assert.Equal(year, t.Year);
        Assert.Equal(month, t.Month);
    }

    [Fact]
    public void SplitsCamelCaseAndKeepsDisplayForm()
    {
        var t = FileNameTokenizer.Tokenize("ProjectPlan_ＡＢＣ商事_コピー.xlsx");
        Assert.Contains("project", t.Words);
        Assert.Contains("plan", t.Words);
        Assert.DoesNotContain("こぴー", t.Words);
        // 大文字略語＋漢字（社名など）は1語のまま、表示用は元の表記
        int i = t.Words.ToList().IndexOf("abc商事");
        Assert.True(i >= 0);
        Assert.Equal("ABC商事", t.DisplayWords[i]);
    }

    [Fact]
    public void KatakanaIsNormalizedToHiragana()
    {
        var t = FileNameTokenizer.Tokenize("テスト計画.docx");
        Assert.Contains("てすと", t.Words);
        Assert.Contains("計画", t.Words);
    }
}

public class SuggestionEngineTests
{
    /// <summary>テスト用のフォルダ木。キーはフォルダのパス、値は直下ファイル名。</summary>
    sealed class MemorySource : IFolderSource
    {
        readonly Dictionary<string, List<string>> _files = new(StringComparer.OrdinalIgnoreCase);

        public MemorySource Add(string folder, params string[] files)
        {
            folder = folder.TrimEnd('\\');
            if (!_files.TryGetValue(folder, out var list)) _files[folder] = list = new();
            list.AddRange(files);
            for (var p = Path.GetDirectoryName(folder); p != null && p.Length > 3; p = Path.GetDirectoryName(p))
                _files.TryAdd(p, new());
            return this;
        }

        public FolderInfo? GetFolder(string path)
        {
            path = path.TrimEnd('\\');
            if (!_files.TryGetValue(path, out var files)) return null;
            var subs = _files.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), path, StringComparison.OrdinalIgnoreCase))
                                  .Select(k => Path.GetFileName(k)).ToList();
            return new FolderInfo(path, files, subs, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        public Task<IReadOnlyList<FolderInfo>> FindCandidatesAsync(IReadOnlyList<string> terms, CancellationToken ct)
        {
            var hits = _files.Where(kv =>
                    terms.Any(t => FileNameTokenizer.Normalize(Path.GetFileName(kv.Key)).Contains(t)
                               || kv.Value.Any(f => FileNameTokenizer.Normalize(f).Contains(t))))
                .Select(kv => GetFolder(kv.Key)!).ToList();
            return Task.FromResult<IReadOnlyList<FolderInfo>>(hits);
        }
    }

    static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0);

    static SuggestionEngine Engine(MemorySource source, List<SaveRecord>? history = null) =>
        new(source, () => history ?? new List<SaveRecord>(), clock: () => Now) { FallbackRoot = @"D:\書類" };

    static MemorySource Office() => new MemorySource()
        .Add(@"D:\書類\請求書\A商事", "2026年08月_請求書_A商事.pdf", "2026年09月_請求書_A商事.pdf")
        .Add(@"D:\書類\請求書\B工業", "請求書_B工業_202607.pdf", "請求書_B工業_202608.pdf")
        .Add(@"D:\書類\見積書", "見積書_A商事.xlsx", "見積書_C物産.xlsx", "見積書_B工業.xlsx")
        .Add(@"D:\写真\旅行", "IMG_0001.jpg", "IMG_0002.jpg")
        .Add(@"D:\仕事\議事録", "議事録_定例会_0901.docx", "議事録_定例会_0915.docx", "議事録_キックオフ.docx");

    [Fact]
    public async Task SuggestsFolderWithSimilarFiles()
    {
        var result = await Engine(Office()).SuggestAsync("2026年10月_請求書_A商事.pdf");
        Assert.Equal(@"D:\書類\請求書\A商事", result.Candidates[0].Path);
        Assert.NotEqual(Confidence.Low, result.Candidates[0].Confidence);
    }

    [Fact]
    public async Task SuggestsByFolderName()
    {
        var result = await Engine(Office()).SuggestAsync("議事録_定例会_1001.docx");
        Assert.Equal(@"D:\仕事\議事録", result.Candidates[0].Path);
    }

    [Fact]
    public async Task ProposesNewFolderForUnknownClient()
    {
        var result = await Engine(Office()).SuggestAsync("2026年10月_請求書_山田商事.pdf");
        Assert.NotNull(result.NewFolder);
        Assert.Equal(@"D:\書類\請求書\山田商事", result.NewFolder!.Path);
        Assert.True(result.NewFolder.IsNew);
    }

    [Fact]
    public async Task NoProposalWhenFolderAlreadyFits()
    {
        var result = await Engine(Office()).SuggestAsync("議事録_定例会_1001.docx");
        Assert.Null(result.NewFolder);
    }

    [Fact]
    public async Task LearnsFromHistory()
    {
        var source = Office().Add(@"D:\書類\経理\提出済み", "memo.txt");
        var name = FileNameTokenizer.Tokenize("請求書_A商事_202610.pdf");
        var history = Enumerable.Range(0, 4)
            .Select(i => new SaveRecord(name.Words, "pdf", @"D:\書類\経理\提出済み", @"D:\書類\請求書\A商事", Now.AddDays(-i)))
            .ToList();
        var result = await Engine(source, history).SuggestAsync("請求書_A商事_202611.pdf");
        Assert.True(@"D:\書類\経理\提出済み" == result.Candidates[0].Path, string.Join(" | ", result.Candidates));
        Assert.Contains("以前", result.Candidates[0].Reason);
    }

    [Fact]
    public async Task DescendsIntoExistingDateFolders()
    {
        var source = new MemorySource()
            .Add(@"D:\経理\領収書", "領収書一覧.xlsx")
            .Add(@"D:\経理\領収書\2025", "領収書_文具.pdf")
            .Add(@"D:\経理\領収書\2026", "領収書_交通費.pdf");
        var result = await Engine(source).SuggestAsync("領収書_タクシー_2026-10-03.pdf");
        Assert.Contains(result.Candidates, c => c.Path == @"D:\経理\領収書\2026");
    }

    [Fact]
    public async Task ProposesMissingDateFolder()
    {
        var source = new MemorySource()
            .Add(@"D:\経理\月次", "月次報告_一覧.xlsx")
            .Add(@"D:\経理\月次\2026-08", "月次報告.xlsx")
            .Add(@"D:\経理\月次\2026-09", "月次報告.xlsx");
        var result = await Engine(source).SuggestAsync("月次報告_2026年10月.xlsx");
        Assert.NotNull(result.NewFolder);
        Assert.Equal(@"D:\経理\月次\2026-10", result.NewFolder!.Path);
    }

    [Fact]
    public async Task ExcludesCurrentAndSystemFolders()
    {
        var source = Office()
            .Add(@"C:\Users\me\Downloads", "請求書_A商事_old.pdf")
            .Add(@"C:\Users\me\AppData\Local\cache", "請求書_A商事.pdf");
        var result = await Engine(source).SuggestAsync("請求書_A商事_202610.pdf", currentFolder: @"C:\Users\me\Downloads");
        Assert.DoesNotContain(result.Candidates, c => c.Path.Contains("Downloads") || c.Path.Contains("AppData"));
    }

    [Fact]
    public async Task FallsBackWhenNothingMatches()
    {
        var result = await Engine(Office()).SuggestAsync("ぜんぜん関係ない企画書.docx");
        Assert.NotNull(result.Best);
    }
}
