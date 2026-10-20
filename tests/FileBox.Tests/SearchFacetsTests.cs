using FileBox.Core.Search;
using FileBox.Core.Tools;
using Xunit;

namespace FileBox.Tests;

public sealed class SearchFacetsTests
{
    static readonly DateTime Today = new(2026, 10, 21);

    [Theory]
    [InlineData("画像", "jpg,png,heic")]
    [InlineData("動画", "mp4,mov")]
    [InlineData("音声", "mp3,flac")]
    [InlineData("Office", "docx,xlsx,pptx")]
    [InlineData("PDF", "pdf,PDF")]
    public void GroupsKindsAndAppliesAllExtensions(string label, string extensions)
    {
        var names = extensions.Split(',').Select((ext, i) => $"AGENTS{i}.{ext}").ToArray();
        var items = names.Select(n => new FacetInput(n, false, 0, null))
            .Append(new FacetInput("フォルダ.jpg", true, 0, null)).ToArray();
        var facet = Assert.Single(SearchFacets.Build(items, Today), f => f.Key == "ext");
        Assert.Equal(label, facet.Label);
        Assert.Equal(names.Length, facet.Count);

        string query = SearchFacets.Apply("AGENTS ext:old in:\"D:\\資料\" -tmp", facet);
        var parsed = QueryParser.Parse(query);
        Assert.Empty(parsed.Errors);
        Assert.Equal(@"D:\資料", parsed.InPath);
        Assert.DoesNotContain("old", parsed.Extensions);
        Assert.Equal(facet.Value.Split(','), parsed.Extensions);
        var compiled = CompiledQuery.Compile(parsed);
        foreach (var name in names) Assert.True(compiled.Match(name, false));
        Assert.False(compiled.Match("AGENTS.tmp", false));
        Assert.False(compiled.Match(names[0], true));
    }

    [Theory]
    [InlineData(ToolKind.ImageConvert, "画像")]
    [InlineData(ToolKind.VideoConvert, "動画")]
    [InlineData(ToolKind.AudioConvert, "音声")]
    public void ReusesMediaClassification(ToolKind kind, string label)
    {
        var extensions = ToolCatalog.Get(kind).Extensions;
        var items = extensions.Select(e => new FacetInput("a." + e, false, 0, null))
            .Append(new FacetInput("a.txt", false, 0, null)).ToArray();
        var facet = Assert.Single(SearchFacets.Build(items, Today), f => f.Label == label);
        Assert.Equal(extensions.Length, facet.Count);
        Assert.Equal(string.Join(',', extensions), facet.Value);
    }

    [Fact]
    public void KeepsUnknownExtensionsLowercaseWithDot()
    {
        var items = new[]
        {
            new FacetInput("a.H", false, 0, null),
            new FacetInput("b.h", false, 0, null),
            new FacetInput("c.CPP", false, 0, null),
            new FacetInput("d.META", false, 0, null),
            new FacetInput("README", false, 0, null),
        };
        var kinds = SearchFacets.Build(items, Today).Where(f => f.Group == "種類").ToArray();
        Assert.Equal(new[] { ".h", ".cpp", ".meta" }, kinds.Select(f => f.Label));
        Assert.Equal(new[] { 2, 1, 1 }, kinds.Select(f => f.Count));
        Assert.Equal("AGENTS ext:h", SearchFacets.Apply("AGENTS ext:cpp", kinds[0]));
    }

    [Fact]
    public void KeepsAllKindsForOverflowAndOrdersByCombinedCount()
    {
        var items = Enumerable.Range(0, 8).Select(i => new FacetInput($"a.ext{i}", false, 0, null))
            .Concat(new[]
            {
                new FacetInput("a.jpg", false, 0, null),
                new FacetInput("b.png", false, 0, null),
                new FacetInput("c.heic", false, 0, null),
                new FacetInput("フォルダ", true, 0, null),
            }).ToArray();
        var kinds = SearchFacets.Build(items, Today).Where(f => f.Group == "種類").ToArray();
        Assert.Equal(10, kinds.Length);
        Assert.Equal("画像", kinds[0].Label);
        Assert.Equal(3, kinds[0].Count);
        Assert.Equal(kinds, SearchFacets.Build(items.Reverse().ToArray(), Today).Where(f => f.Group == "種類"));
        Assert.Equal(kinds.Take(5), SearchFacets.Build(items, Today, maxKinds: 5).Where(f => f.Group == "種類"));
        Assert.Contains(kinds, f => f.Key == "folder" && SearchFacets.Apply("AGENTS", f) == "folder: AGENTS");
    }

    [Fact]
    public void OmitsEmptyAndNonFilteringKinds()
    {
        Assert.Empty(SearchFacets.Build(Array.Empty<FacetInput>(), Today));
        var items = new[] { new FacetInput("a.jpg", false, 0, null), new FacetInput("b.png", false, 0, null) };
        Assert.DoesNotContain(SearchFacets.Build(items, Today), f => f.Group == "種類");
    }
}
