using FileBox.Core.Search;
using Xunit;

namespace FileBox.Tests;

public class QueryParserTests
{
    static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0);

    [Fact]
    public void ParsesDesignExample()
    {
        var q = QueryParser.Parse("請求書 ext:pdf,xlsx size:>1MB modified:2026-10 in:\"D:\\書類\" folder: -tmp", Now);
        Assert.Equal(KindFilter.Folders, q.Kind);
        Assert.Equal(new[] { "pdf", "xlsx" }, q.Extensions);
        Assert.Equal((1L << 20) + 1, q.MinSize);
        Assert.Null(q.MaxSize);
        Assert.Equal(new DateTime(2026, 10, 1), q.ModifiedFrom);
        Assert.Equal(new DateTime(2026, 11, 1), q.ModifiedTo);
        Assert.Equal("D:\\書類", q.InPath);
        Assert.Single(q.Groups);
        Assert.Equal(2, q.Groups[0].Count);
        Assert.Equal(new SearchTerm("請求書", null, false, false), q.Groups[0][0]);
        Assert.Equal(new SearchTerm("tmp", null, true, false), q.Groups[0][1]);
        Assert.Empty(q.Errors);
    }

    [Fact]
    public void ParsesTermModifiers()
    {
        var q = QueryParser.Parse("^rep \"a.txt\" ~seikyuu report_*.xlsx regex:^\\d+$ path:書類\\2026");
        var t = q.Groups[0];
        Assert.Equal(MatchMode.Prefix, t[0].Mode);
        Assert.Equal(MatchMode.Exact, t[1].Mode);
        Assert.Equal("a.txt", t[1].Text);
        Assert.Equal(MatchMode.Fuzzy, t[2].Mode);
        Assert.Equal(MatchMode.Wildcard, t[3].Mode);
        Assert.Equal(MatchMode.Regex, t[4].Mode);
        Assert.Equal("^\\d+$", t[4].Text);
        Assert.True(t[5].OnPath);
    }

    [Fact]
    public void ParsesOrGroups()
    {
        var q = QueryParser.Parse("a b | c OR d");
        Assert.Equal(3, q.Groups.Count);
        Assert.Equal(2, q.Groups[0].Count);
        Assert.Single(q.Groups[1]);
        Assert.Single(q.Groups[2]);
    }

    [Fact]
    public void DriveLetterIsNotAKey()
    {
        var q = QueryParser.Parse("C:\\Windows");
        Assert.Equal("C:\\Windows", q.Groups[0][0].Text);
    }

    [Theory]
    [InlineData("10MB..1GB", 10L << 20, 1L << 30)]
    [InlineData("<100KB", null, (100L << 10) - 1)]
    [InlineData(">=2k", 2048L, null)]
    [InlineData("1.5MB", 1572864L, null)]
    public void ParsesSizes(string text, long? min, long? max)
    {
        Assert.True(QueryParser.TryParseSize(text, out var a, out var b));
        Assert.Equal(min, a);
        Assert.Equal(max, b);
    }

    [Fact]
    public void ParsesDateKeywords()
    {
        var q = QueryParser.Parse("modified:thisweek", Now); // 2026-10-18 は日曜
        Assert.Equal(new DateTime(2026, 10, 12), q.ModifiedFrom);
        Assert.Equal(new DateTime(2026, 10, 19), q.ModifiedTo);

        q = QueryParser.Parse("modified:>2026-01-01", Now);
        Assert.Equal(new DateTime(2026, 1, 2), q.ModifiedFrom);
        Assert.Null(q.ModifiedTo);

        q = QueryParser.Parse("dm:2025..2026-03", Now);
        Assert.Equal(new DateTime(2025, 1, 1), q.ModifiedFrom);
        Assert.Equal(new DateTime(2026, 4, 1), q.ModifiedTo);
    }

    [Fact]
    public void ReportsErrors()
    {
        var q = QueryParser.Parse("size:abc modified:someday mode:magic");
        Assert.Equal(3, q.Errors.Count);
        var c = CompiledQuery.Compile("regex:[");
        Assert.Single(c.Errors);
    }

    [Fact]
    public void EditorSyncsChips()
    {
        string s = QueryEditor.SetKind("請求書 ext:pdf", KindFilter.Folders);
        Assert.Equal("folder: 請求書 ext:pdf", s);
        Assert.Equal(KindFilter.Folders, QueryParser.Parse(s).Kind);

        s = QueryEditor.SetKind("folder:請求書", KindFilter.All);
        Assert.Equal("請求書", s);

        s = QueryEditor.SetMode("abc", MatchMode.Prefix);
        Assert.Equal(MatchMode.Prefix, QueryParser.Parse(s).DefaultMode);
        Assert.Equal("abc", QueryEditor.SetMode(s, MatchMode.Substring));

        s = QueryEditor.SetValue("abc ext:txt dm:today", "ext", "pdf,docx");
        Assert.Equal("abc dm:today ext:pdf,docx", s);
        s = QueryEditor.SetValue(s, "modified", "thisweek");
        Assert.Equal("abc ext:pdf,docx modified:thisweek", s);
        Assert.Equal("abc modified:thisweek", QueryEditor.SetValue(s, "ext", null));

        s = QueryEditor.SetInPath("abc", "D:\\My Docs");
        Assert.Equal("D:\\My Docs", QueryParser.Parse(s).InPath);
        Assert.Equal("abc", QueryEditor.SetInPath(s, null));
    }
}

public class MatcherTests
{
    [Theory]
    [InlineData("report", "Annual_Report.pdf", true)]
    [InlineData("^ann", "Annual_Report.pdf", true)]
    [InlineData("^rep", "Annual_Report.pdf", false)]
    [InlineData("\"annual_report.pdf\"", "Annual_Report.pdf", true)]
    [InlineData("\"annual\"", "Annual_Report.pdf", false)]
    [InlineData("*.pdf", "Annual_Report.pdf", true)]
    [InlineData("a??ual*", "Annual_Report.pdf", true)]
    [InlineData("regex:^\\w+_\\w+\\.pdf$", "Annual_Report.pdf", true)]
    [InlineData("report -annual", "Annual_Report.pdf", false)]
    [InlineData("xyz | report", "Annual_Report.pdf", true)]
    [InlineData("mode:prefix rep", "Annual_Report.pdf", false)]
    [InlineData("ＲＥＰＯＲＴ", "Annual_Report.pdf", true)]
    [InlineData("ext:pdf", "Annual_Report.pdf", true)]
    [InlineData("ext:docx,xlsx", "Annual_Report.pdf", false)]
    public void Matches(string query, string name, bool expected)
    {
        Assert.Equal(expected, CompiledQuery.Compile(query).Match(name, false));
    }

    [Fact]
    public void KindFilters()
    {
        Assert.False(CompiledQuery.Compile("folder: a").Match("a.txt", false));
        Assert.True(CompiledQuery.Compile("folder: a").Match("abc", true));
        Assert.False(CompiledQuery.Compile("file: a").Match("abc", true));
        Assert.False(CompiledQuery.Compile("ext:txt").Match("x.txt", true));
    }

    [Theory]
    [InlineData("~seikyuu", "2026年_請求書.pdf", false)] // 漢字はローマ字から引けない
    [InlineData("~seikyuu", "せいきゅう一覧.xlsx", true)]
    [InlineData("~seikyuu", "セイキュウ.txt", true)]
    [InlineData("~shashin", "しゃしん", true)]
    [InlineData("~kitte", "きって.png", true)]
    [InlineData("~konnichiha", "こんにちは.txt", true)]
    [InlineData("~しゃしん", "シャシン.jpg", true)]
    public void FuzzyMatches(string query, string name, bool expected)
    {
        Assert.Equal(expected, CompiledQuery.Compile(query).Match(name, false));
    }

    [Fact]
    public void RomajiConversion()
    {
        Assert.Equal("しんぶん", KanaConverter.RomajiToHiragana("shinbun"));
        Assert.Equal("がっこう", KanaConverter.RomajiToHiragana("gakkou"));
        Assert.Equal("きょうと", KanaConverter.RomajiToHiragana("kyouto"));
        Assert.Equal("ほんや", KanaConverter.RomajiToHiragana("hon'ya"));
    }

    [Fact]
    public void Ranks()
    {
        var q = CompiledQuery.Compile("report");
        Assert.Equal(0, q.Rank("report.pdf"));
        Assert.Equal(1, q.Rank("report_2026.pdf"));
        Assert.Equal(2, q.Rank("annual_report.pdf"));
    }

    [Fact]
    public void Metadata()
    {
        var q = CompiledQuery.Compile("size:1KB..1MB modified:2026-10");
        Assert.True(q.MatchMetadata(4096, new DateTime(2026, 10, 5)));
        Assert.False(q.MatchMetadata(10, new DateTime(2026, 10, 5)));
        Assert.False(q.MatchMetadata(4096, new DateTime(2026, 11, 1)));
        Assert.False(q.MatchMetadata(4096, null));
    }
}
