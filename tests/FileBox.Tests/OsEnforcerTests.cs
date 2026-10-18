using System.Security.AccessControl;
using System.Security.Principal;
using FileBox.Core.Security;
using FileBox.Indexer.Policy;
using Xunit;

namespace FileBox.Tests;

public sealed class OsEnforcerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "filebox-acl-" + Guid.NewGuid().ToString("N"));

    public OsEnforcerTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void MapsPermissionsToNtfsRights()
    {
        Assert.True(OsEnforcer.RightsFor(Permission.Create).HasFlag(FileSystemRights.CreateFiles));
        Assert.True(OsEnforcer.RightsFor(Permission.Delete).HasFlag(FileSystemRights.Delete));
        Assert.Equal((FileSystemRights)0, OsEnforcer.RightsFor(Permission.Share));   // リンク発行は OS では表せない
    }

    [Fact]
    public void AppliesAndRemovesOnlyItsOwnDenyEntry()
    {
        // 自分の一時フォルダで「作成」だけを拒否して外す（削除・読み取りは拒否しないので後片付けできる）
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        var rights = OsEnforcer.RightsFor(Permission.Create);
        OsEnforcer.ApplyDeny(_dir, sid, rights);
        Assert.True(OsEnforcer.HasDeny(_dir, sid, rights));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(_dir, "x.txt"), "x"));

        OsEnforcer.RemoveDeny(_dir, sid, rights);
        Assert.False(OsEnforcer.HasDeny(_dir, sid, rights));
        File.WriteAllText(Path.Combine(_dir, "x.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
