using FileBox.Data;
using Xunit;

namespace FileBox.Tests;

public sealed class UserStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"filebox-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void PersistsAcrossReopen()
    {
        using (var s = new UserStore(_path))
        {
            s.SetSetting("theme", "dark");
            s.AddFavorite(@"D:\書類");
            s.AddFavorite(@"d:\書類"); // 大文字小文字違いは重複扱い
            s.SaveWorkspace("請求書整理", "{\"tabs\":[]}");
            s.SaveWorkspace(UserStore.SessionWorkspace, "{}");
            s.AddSearchHistory("a");
            s.AddSearchHistory("b");
            s.AddSearchHistory("a");
        }
        using (var s = new UserStore(_path))
        {
            Assert.Equal("dark", s.GetSetting("theme"));
            Assert.Single(s.GetFavorites());
            Assert.Equal("請求書整理", s.GetWorkspaces().Single().Name); // セッション用は一覧に出ない
            Assert.NotNull(s.GetWorkspace(UserStore.SessionWorkspace));
            Assert.Equal(new[] { "a", "b" }, s.GetSearchHistory());
        }
    }

    [Fact]
    public void StoresSaveHistoryAndInbox()
    {
        using var s = new UserStore(_path);
        s.AddSaveHistory("請求書 山田商事", "pdf", @"D:\書類\請求書\山田商事", 1, null);
        s.AddSaveHistory("議事録", "docx", @"D:\仕事\議事録", null, @"D:\仕事");
        var history = s.GetSaveHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal(@"D:\仕事", history[0].RejectedPath); // 新しい順

        s.AddInboxSource(@"C:\Users\me\Downloads");
        s.AddInboxSource(@"c:\users\me\downloads");
        Assert.Single(s.GetInboxSources());

        s.AddInboxRule("請求書*", "pdf", @"D:\書類\請求書", autoMove: false);
        var rule = s.GetInboxRules().Single();
        s.SetInboxRuleAutoMove(rule.Id, true);
        Assert.True(s.GetInboxRules().Single().AutoMove);

        s.DismissInboxItem(@"C:\Users\me\Downloads\a.zip");
        Assert.Contains(@"c:\users\me\downloads\A.ZIP", s.GetDismissedInboxItems());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { }
    }
}
