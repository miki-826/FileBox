using FileBox.Core.Security;
using FileBox.Data;
using Xunit;

namespace FileBox.Tests;

public sealed class SystemStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"filebox-system-{Guid.NewGuid():N}.db");

    [Fact]
    public void FirstUserAndWindowsAdminsBecomeAdmins()
    {
        using var s = new SystemStore(_path);
        var (first, created) = s.EnsureUser("S-1-5-21-1", @"PC\hanako", isWindowsAdmin: false);
        Assert.True(created);
        Assert.Equal(SystemStore.AdminRole, first.RoleId);

        var (taro, _) = s.EnsureUser("S-1-5-21-2", @"PC\taro", isWindowsAdmin: false);
        Assert.Equal(SystemStore.StandardRole, taro.RoleId);

        s.SetSetting("defaultRole", SystemStore.GuestRole.ToString());
        var (child, _) = s.EnsureUser("S-1-5-21-3", @"PC\child", isWindowsAdmin: false);
        Assert.Equal(SystemStore.GuestRole, child.RoleId);

        var (again, createdAgain) = s.EnsureUser("S-1-5-21-2", @"PC\taro-renamed", isWindowsAdmin: false);
        Assert.False(createdAgain);
        Assert.Equal(@"PC\taro-renamed", again.AccountName);
    }

    [Fact]
    public void StoresPermissionsZonesOwnersAndAudit()
    {
        using var s = new SystemStore(_path);
        var (u, _) = s.EnsureUser("S-1", @"PC\a", false);

        s.SetPermission(@"D:\Private\", null, SystemStore.StandardRole, Permission.None, Permission.All);
        s.SetPermission(@"d:\private", null, SystemStore.StandardRole, Permission.Read, Permission.None);   // 同じ場所は上書き
        var rule = Assert.Single(s.GetPermissions()).Rule;
        Assert.Equal(@"d:\private", rule.Path);
        Assert.Equal(Permission.Read, rule.Allow);

        long id = s.SaveZone(new QuotaZone(0, @"D:\写真", 200L << 30, "all", null, null, 80));
        s.SaveZone(new QuotaZone(id, @"D:\写真", 300L << 30, "all", null, null, 90));
        var zone = Assert.Single(s.GetZones());
        Assert.Equal(300L << 30, zone.LimitBytes);

        s.RecordOwner(7, new byte[16], u.Id, @"D:\a.txt", 100);
        s.RecordOwner(7, new byte[16], u.Id, @"D:\b.txt", 150);
        Assert.Equal(150, Assert.Single(s.GetOwnedFiles(u.Id)).Size);

        s.Audit(u.Id, "move", @"C:\x", @"D:\y", 10, "ok");
        s.Audit(u.Id, "delete", @"C:\z", null, null, "denied");
        Assert.Equal(2, s.GetAudit().Count);
        Assert.Equal("denied", Assert.Single(s.GetAudit(action: "delete")).Result);
        Assert.Equal(@"PC\a", s.GetAudit()[0].Account);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { }
    }
}
