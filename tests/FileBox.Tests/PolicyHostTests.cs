using FileBox.Contracts;
using FileBox.Indexer.Index;
using FileBox.Indexer.Policy;
using Xunit;

namespace FileBox.Tests;

public sealed class PolicyHostTests : IDisposable
{
    readonly string _db = Path.Combine(Path.GetTempPath(), $"filebox-policy-{Guid.NewGuid():N}", "system.db");

    static FileIndex Sample()
    {
        var ix = new FileIndex();
        using var w = ix.Write();
        int root = w.AddRoot(0, "D:", 0);
        int photos = w.Add(root, "写真", EntryFlags.Directory, 0, 0);
        w.Add(photos, "a.jpg", EntryFlags.None, 700, 0);
        int sub = w.Add(photos, "2026", EntryFlags.Directory, 0, 0);
        w.Add(sub, "b.mp4", EntryFlags.None, 300, 0);
        return ix;
    }

    [Fact]
    public void RegistersCallersAndEnforcesAdminRole()
    {
        using var host = new PolicyHost(Sample(), elevated: false, _db);
        var owner = new Caller("S-1-5-21-100", @"PC\owner", IsWindowsAdmin: false);
        var taro = new Caller("S-1-5-21-200", @"PC\taro", IsWindowsAdmin: false);

        var s1 = PolicyMethods.FromNode<SessionInfo>(host.Handle(owner, PolicyMethods.Session, null))!;
        Assert.True(s1.UserId > 0);
        Assert.True(s1.IsAdmin);          // 最初の利用者は管理者
        Assert.True(s1.FirstUse);

        var s2 = PolicyMethods.FromNode<SessionInfo>(host.Handle(taro, PolicyMethods.Session, null))!;
        Assert.False(s2.IsAdmin);
        Assert.Throws<UnauthorizedAccessException>(() =>
            host.Handle(taro, PolicyMethods.AdminSaveZone, PolicyMethods.ToNode(new ZoneDto { Path = @"D:\写真", LimitBytes = 1500 })));

        // 管理者がゾーンを作ると、利用者のセッションに使用量付きで載る
        host.Handle(owner, PolicyMethods.AdminSaveZone, PolicyMethods.ToNode(new ZoneDto { Path = @"D:\写真", LimitBytes = 1500, Scope = "all" }));
        var s3 = PolicyMethods.FromNode<SessionInfo>(host.Handle(taro, PolicyMethods.Session, null))!;
        var zone = Assert.Single(s3.Zones);
        Assert.Equal(1000, zone.UsedBytes);

        var audit = PolicyMethods.FromNode<List<AuditDto>>(host.Handle(owner, PolicyMethods.AdminAudit, null))!;
        Assert.Contains(audit, a => a.Action == "quota_change");
        Assert.Equal(2, audit.Count(a => a.Action == "user_register"));
    }

    [Fact]
    public void AnonymousCallerCannotAdminister()
    {
        using var host = new PolicyHost(Sample(), elevated: false, _db);
        host.Handle(new Caller("S-1", "first", false), PolicyMethods.Session, null);
        var anon = new Caller("", "(不明)", false);
        Assert.Throws<UnauthorizedAccessException>(() => host.Handle(anon, PolicyMethods.AdminUsers, null));
    }

    [Fact]
    public void LastAdminCannotDemoteThemselves()
    {
        using var host = new PolicyHost(Sample(), elevated: false, _db);
        var owner = new Caller("S-1", @"PC\owner", false);
        var s = PolicyMethods.FromNode<SessionInfo>(host.Handle(owner, PolicyMethods.Session, null))!;
        Assert.Throws<InvalidOperationException>(() => host.Handle(owner, PolicyMethods.AdminUpdateUser,
            PolicyMethods.ToNode(new UserDto { Id = s.UserId, Account = s.Account, RoleId = 2 })));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_db)!, recursive: true); } catch { }
    }
}
