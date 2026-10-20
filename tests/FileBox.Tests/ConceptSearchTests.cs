using FileBox.Core.Search;
using Xunit;

namespace FileBox.Tests;

public class ConceptSearchTests
{
    [Theory]
    [InlineData("ゲーム", "ゲーム")]
    [InlineData("げーむ", "ゲーム")]
    [InlineData("Games", "ゲーム")]
    [InlineData("ｹﾞｰﾑ", "ゲーム")]
    [InlineData("メモ", "メモ")]
    [InlineData("kind:メモ", "メモ")]
    [InlineData("種類:写真", "写真")]
    [InlineData("スクショ", "スクリーンショット")]
    public void FindsConceptFromSingleWord(string query, string expected) =>
        Assert.Equal(expected, ConceptSearch.Find(query)?.Name);

    [Theory]
    [InlineData("")]
    [InlineData("ゲーム ext:exe")]
    [InlineData("ゲーム実況")]
    [InlineData("folder:ゲーム")]
    [InlineData("kind:なにか")]
    public void IgnoresOtherQueries(string query) => Assert.Null(ConceptSearch.Find(query));

    [Fact]
    public void GameQueriesLookForLibrariesAndNames()
    {
        var game = ConceptSearch.Find("ゲーム")!;
        var queries = game.IndexQueries().ToList();
        Assert.Contains(queries, q => q.StartsWith("folder: ") && q.Contains("ゲーム") && q.Contains("game"));
        // ライブラリは最後の段の名前と、手前の段のパスで探す。どれも構文として正しい
        Assert.Contains(queries, q => q.Contains("\"common\" path:steamapps") && q.Contains("\"Epic Games\"") && q.Contains("path:\"GOG Galaxy\""));
        Assert.All(queries, q => Assert.Empty(QueryParser.Parse(q).Errors));

        Assert.Equal("Steam", game.LibraryLabel(@"D:\SteamLibrary\steamapps\common"));
        Assert.Equal("Epic Games", game.LibraryLabel(@"C:\Program Files\Epic Games\"));
        Assert.Null(game.LibraryLabel(@"D:\SteamLibrary\steamapps\common\Portal 2"));
        // ゲーム本体の奥（GameData など）は名前で当たっても出さない
        Assert.False(game.IsInsideLibrary(@"D:\SteamLibrary\steamapps\common\Portal 2"));
        Assert.True(game.IsInsideLibrary(@"D:\SteamLibrary\steamapps\common\Portal 2\GameData"));
        Assert.False(game.IsInsideLibrary(@"C:\Users\a\Documents\My Games"));
    }

    [Fact]
    public void ScoresCloseNamesHigherAndSystemPlacesLower()
    {
        var memo = ConceptSearch.Find("メモ")!;
        double exact = memo.Score("メモ", @"C:\Users\a\Documents\メモ", true);
        double prefix = memo.Score("メモ_2026.txt", @"C:\Users\a\Documents\メモ_2026.txt", false);
        double inner = memo.Score("会議のメモ書き.docx", @"C:\Users\a\Documents\会議のメモ書き.docx", false);
        double system = memo.Score("Notes", @"C:\Users\a\AppData\Local\Some\Notes", true);
        Assert.True(exact > prefix && prefix > inner, $"{exact} {prefix} {inner}");
        Assert.True(system < inner);
        Assert.Equal(0, memo.Score("写真", @"C:\写真", true));
        // 開発の部品・キャッシュはメモとして出さない
        Assert.Equal(0, memo.Score("notes", @"C:\src\app\node_modules\notes", true));
        Assert.Equal(0, memo.Score("Note.cs", @"C:\src\app\Note.cs", false));
        Assert.Equal("Obsidian の保管庫", memo.MarkerLabel(".obsidian"));
    }

    [Fact]
    public void InstallerIncludesMsiByExtension()
    {
        var installer = ConceptSearch.Find("インストーラー")!;
        Assert.Contains("file: ext:msi,msix,appx", installer.IndexQueries());
        Assert.True(installer.Score("tool.msi", @"C:\Users\a\Downloads\tool.msi", false) > 0);
    }

    [Fact]
    public void KindKeyIsSuggested() =>
        Assert.Contains(QueryHints.For("ki"), h => h.Label == "kind:");
}
