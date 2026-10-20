using FileBox.Core.Preview;
using FileBox.Core.Search;
using FileBox.Core.Settings;
using Xunit;

namespace FileBox.Tests;

public sealed class SearchExtrasTests
{
    [Theory]
    [InlineData("ふぁいる", "ファイル一覧.txt", true)]
    [InlineData("ファイル", "ふぁいる.txt", true)]
    [InlineData("shorui", "ショルイ.pdf", true)]
    [InlineData("shorui", "shorui.pdf", true)]
    [InlineData("ｶﾀｶﾅ", "カタカナ.txt", true)]
    [InlineData("report", "れぽ.txt", false)]
    public void DefaultSearchAbsorbsKanaVariants(string query, string name, bool expected)
    {
        Assert.Equal(expected, CompiledQuery.Compile(query).Match(name, false));
    }

    [Fact]
    public void HighlightsMatches()
    {
        var q = QueryParser.Parse("請求 2026");
        Assert.Equal(new[] { (0, 2), (3, 4) }, MatchHighlighter.Ranges(q, "請求_2026年.pdf"));
        Assert.Equal(new[] { (0, 4) }, MatchHighlighter.Ranges(QueryParser.Parse("ふぁいる"), "ファイル.txt"));
        Assert.Empty(MatchHighlighter.Ranges(QueryParser.Parse("-請求"), "請求.pdf"));
        Assert.Equal(new[] { (4, 3) }, MatchHighlighter.Ranges(QueryParser.Parse("regex:\\d+"), "img_001.jpg"));
    }

    [Fact]
    public void BuildsFacets()
    {
        var today = new DateTime(2026, 10, 20);
        var items = new[]
        {
            new FacetInput("a.pdf", false, 50_000, today.AddHours(9)),
            new FacetInput("b.pdf", false, 5_000_000, today.AddDays(-40)),
            new FacetInput("c.docx", false, 2_000, today.AddDays(-1)),
            new FacetInput("資料", true, 0, today.AddDays(-400)),
        };
        var facets = SearchFacets.Build(items, today);
        Assert.Contains(facets, f => f.Key == "ext" && f.Value == "pdf" && f.Count == 2);
        Assert.Contains(facets, f => f.Key == "folder" && f.Count == 1);
        Assert.Contains(facets, f => f.Key == "modified" && f.Value == "today" && f.Count == 1);
        Assert.Contains(facets, f => f.Key == "size" && f.Value == "<100KB" && f.Count == 2);

        var pdf = facets.First(f => f.Value == "pdf");
        Assert.Equal("請求 ext:pdf", SearchFacets.Apply("請求", pdf));
        var q = QueryParser.Parse(SearchFacets.Apply("請求", facets.First(f => f.Key == "size" && f.Value == "100KB..10MB")));
        Assert.Equal(100L << 10, q.MinSize);
        Assert.Empty(q.Errors);
    }

    [Fact]
    public void SuggestsQuerySyntax()
    {
        Assert.Contains(QueryHints.For("請求 ex"), h => h.Query == "請求 ext:");
        Assert.Contains(QueryHints.For("modified:this"), h => h.Query == "modified:thisweek ");
        Assert.Contains(QueryHints.For("size:"), h => h.Label == "size:>100MB");
        Assert.Empty(QueryHints.For("請求 "));
        foreach (var h in QueryHints.For("modified:"))
            Assert.Empty(QueryParser.Parse(h.Query).Errors);
    }

    [Fact]
    public void HighlightsCode()
    {
        var tokens = SyntaxHighlighter.Tokenize("public int x = 42; // 答え\nvar s = \"文字\";", ".cs");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword && t.Text == "public");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Number && t.Text == "42");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && t.Text == "// 答え");
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text == "\"文字\"");
        Assert.Equal("public int x = 42; // 答え\nvar s = \"文字\";", string.Concat(tokens.Select(t => t.Text)));
        Assert.Single(SyntaxHighlighter.Tokenize("abc", ".unknown"));
    }

    [Fact]
    public void RendersMarkdownSafely()
    {
        string html = Markdown.ToHtml("# 見出し\n\n本文 **太字** と `code` と my_file_name\n\n- 項目\n- [x] 済み\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n<script>alert(1)</script>\n[危険](javascript:alert(1)) [安全](https://example.com)");
        Assert.Contains("<h1>見出し</h1>", html);
        Assert.Contains("<strong>太字</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("my_file_name", html);
        Assert.Contains("<li>☑ 済み</li>", html);
        Assert.Contains("<td>1</td>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html.Replace("&lt;script&gt;", ""));
        Assert.Contains("<a href=\"https://example.com\">安全</a>", html);
    }

    [Fact]
    public void SettingsPackageRoundTripDropsEnvironmentSettings()
    {
        var p = new SettingsPackage
        {
            ExportedAt = new DateTime(2026, 10, 20),
            Settings = new() { ["showHidden"] = "1", ["explorer.replace"] = "1", ["links.httpSecret"] = "s" },
            Favorites = new() { new(@"C:\仕事", "仕事") },
            Tags = new() { new(@"C:\a.txt", "重要") },
        };
        var back = SettingsPackage.Parse(p.ToJson(), out var error);
        Assert.Null(error);
        Assert.NotNull(back);
        Assert.Equal("1", back!.Settings["showHidden"]);
        Assert.False(back.Settings.ContainsKey("explorer.replace"));
        Assert.False(back.Settings.ContainsKey("links.httpSecret"));
        Assert.Equal("仕事", back.Favorites[0].Label);
        Assert.Equal("重要", back.Tags[0].Name);

        Assert.Null(SettingsPackage.Parse("{\"format\":\"other\"}", out error));
        Assert.NotNull(error);
        Assert.Null(SettingsPackage.Parse("{\"format\":\"FileBox.Settings\",\"version\":99}", out error));
        Assert.Null(SettingsPackage.Parse("not json", out error));
    }
}
