using FileBox.Data;
using Xunit;

namespace FileBox.Tests;

public sealed class UserDataExtrasTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "filebox-data-" + Guid.NewGuid().ToString("N"));

    public UserDataExtrasTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void TagsStarsFollowRenames()
    {
        using var store = new UserStore(Path.Combine(_dir, "u.db"));
        store.AddTag(@"C:\仕事\見積.xlsx", "重要");
        store.AddTag(@"C:\仕事\見積.xlsx", "顧客A");
        store.AddTag(@"C:\仕事", "フォルダ");
        store.AddTag(@"C:\仕事2\x.txt", "重要");
        store.SetStar(@"C:\仕事\見積.xlsx", true);
        store.AddFavorite(@"C:\仕事");

        Assert.Equal(new[] { "フォルダ", "重要", "顧客A" }.OrderBy(s => s, StringComparer.Ordinal), store.GetTagNames().OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(2, store.GetTaggedPaths("重要").Count);

        store.RenamePath(@"C:\仕事", @"D:\業務");
        Assert.Equal(new[] { "重要", "顧客A" }.OrderBy(s => s, StringComparer.Ordinal), store.GetTags(@"D:\業務\見積.xlsx").OrderBy(s => s, StringComparer.Ordinal));
        Assert.Contains(@"D:\業務\見積.xlsx", store.GetStars());
        Assert.Equal(@"D:\業務", store.GetFavorites()[0].Path);
        // 名前が前方一致するだけの別フォルダは付け替えない
        Assert.Single(store.GetTags(@"C:\仕事2\x.txt"));

        store.RemoveTag(@"D:\業務\見積.xlsx", "顧客A");
        Assert.Single(store.GetTags(@"D:\業務\見積.xlsx"));
        store.SetStar(@"D:\業務\見積.xlsx", false);
        Assert.Empty(store.GetStars());
    }

    [Fact]
    public void LearnedFoldersAndExclusions()
    {
        using var store = new UserStore(Path.Combine(_dir, "u.db"));
        store.AddSaveHistory("請求", "pdf", @"C:\経理", 1, null);
        store.AddSaveHistory("請求", "pdf", @"C:\経理", 1, null);
        store.AddSaveHistory("写真", "jpg", @"C:\写真", 1, null);
        var learned = store.GetLearnedFolders();
        Assert.Equal(@"C:\経理", learned[0].FolderPath);
        Assert.Equal(2, learned[0].Count);
        store.ForgetLearnedFolder(@"c:\経理");
        Assert.Single(store.GetLearnedFolders());

        store.SetSuggestExcluded(@"C:\一時", true);
        store.SetSuggestExcluded(@"C:\一時", true);
        Assert.Single(store.GetSuggestExcluded());
        store.SetSuggestExcluded(@"C:\一時", false);
        Assert.Empty(store.GetSuggestExcluded());
    }

    [Fact]
    public void FrequentSearchesNeedThreeUses()
    {
        using var store = new UserStore(Path.Combine(_dir, "u.db"));
        for (int i = 0; i < 3; i++) store.AddSearchHistory("ext:pdf 請求");
        store.AddSearchHistory("一回だけ");
        var frequent = store.GetFrequentSearches();
        Assert.Single(frequent);
        Assert.Equal(3, frequent[0].Count);
        store.SaveSearch("請求", "ext:pdf 請求");
        Assert.Empty(store.GetFrequentSearches());
    }

    [Fact]
    public void ContentIndexFindsJapaneseText()
    {
        using var content = new ContentStore(Path.Combine(_dir, "c.db"));
        var t = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        content.Upsert(@"C:\a\見積.txt", 10, t, "株式会社山田商事 御中\n見積金額は 120,000 円です");
        content.Upsert(@"C:\a\議事録.txt", 10, t, "次回の会議は月曜日");
        Assert.True(content.IsCurrent(@"C:\a\見積.txt", 10, t));
        Assert.False(content.IsCurrent(@"C:\a\見積.txt", 11, t));

        var hits = content.Search("山田商事");
        Assert.Single(hits);
        Assert.Equal(@"C:\a\見積.txt", hits[0].Path);
        Assert.Contains("「山田商事」", hits[0].Snippet);
        Assert.Single(content.Search("会議"));            // 2 文字（trigram の索引を使わない検索）
        Assert.Single(content.Search("見積金額 円です"));  // 複数の語はすべて含むもの
        Assert.Empty(content.Search("存在しない語"));

        content.Upsert(@"C:\a\見積.txt", 12, t, "差し替えた本文");
        Assert.Empty(content.Search("山田商事"));
        content.RemoveUnder(@"C:\a");
        Assert.Equal(0, content.Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
