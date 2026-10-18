using FileBox.Core.Security;
using Xunit;

namespace FileBox.Tests;

public class PolicyTests
{
    const long Taro = 2, Standard = 2, Guest = 3;
    const Permission StandardDefault = Permission.Read | Permission.Create | Permission.Modify | Permission.Delete | Permission.Share;

    [Fact]
    public void RoleDefaultAppliesWithoutRules()
    {
        Assert.Equal(StandardDefault, PolicyEvaluator.Effective(@"D:\書類", Taro, Standard, StandardDefault, new List<PermissionRule>()));
        Assert.Equal(Permission.Read, PolicyEvaluator.Effective(@"D:\書類", 9, Guest, Permission.Read, new List<PermissionRule>()));
    }

    [Fact]
    public void DeepestRuleWinsAndIsInherited()
    {
        var rules = new List<PermissionRule>
        {
            new(@"D:\private", Permission.None, Permission.All, RoleId: Standard),
            new(@"D:\private\shared", Permission.Read, Permission.None, RoleId: Standard),
        };
        Assert.Equal(Permission.None, PolicyEvaluator.Effective(@"D:\private\日記.txt", Taro, Standard, StandardDefault, rules));
        Assert.Equal(Permission.Read, PolicyEvaluator.Effective(@"D:\Private\Shared\a.pdf", Taro, Standard, StandardDefault, rules));
        Assert.Equal(StandardDefault, PolicyEvaluator.Effective(@"D:\privateX", Taro, Standard, StandardDefault, rules)); // 名前の前方一致は配下ではない
    }

    [Fact]
    public void UserRuleOverridesRoleRuleAndDenyWinsAtSameLevel()
    {
        var rules = new List<PermissionRule>
        {
            new(@"D:\書類", Permission.None, Permission.Delete, RoleId: Standard),
            new(@"D:\書類", Permission.Delete, Permission.None, UserId: Taro),
            new(@"D:\書類\経理", Permission.Create, Permission.Create, UserId: Taro),
        };
        var p = PolicyEvaluator.Effective(@"D:\書類\見積.xlsx", Taro, Standard, StandardDefault, rules);
        Assert.True(p.HasFlag(Permission.Delete));          // 個人設定がロールの拒否より優先
        var q = PolicyEvaluator.Effective(@"D:\書類\経理\a.xlsx", Taro, Standard, StandardDefault, rules);
        Assert.False(q.HasFlag(Permission.Create));         // 同じ深さの許可と拒否は拒否
        Assert.False(PolicyEvaluator.Effective(@"D:\書類\a", 99, Standard, StandardDefault, rules).HasFlag(Permission.Delete)); // 他人はロール設定
    }

    [Fact]
    public void QuotaBlocksOnlyIncomingBytes()
    {
        var zones = new[] { new QuotaZone(1, @"D:\写真", 1000, "all", null, null, 80, UsedBytes: 900) };
        Assert.NotNull(QuotaCheck.FindViolation(zones, @"D:\写真\2026", new[] { (@"C:\Users\me\Downloads\a.jpg", 200L) }, move: true));
        Assert.Null(QuotaCheck.FindViolation(zones, @"D:\写真\2026", new[] { (@"C:\Users\me\Downloads\a.jpg", 50L) }, move: true));
        // ゾーン内の移動は増えない
        Assert.Null(QuotaCheck.FindViolation(zones, @"D:\写真\2026", new[] { (@"D:\写真\2025\big.mp4", 5000L) }, move: true));
        // コピーは増える
        Assert.NotNull(QuotaCheck.FindViolation(zones, @"D:\写真\2026", new[] { (@"D:\写真\2025\big.mp4", 5000L) }, move: false));
        // ゾーン外は対象外
        Assert.Null(QuotaCheck.FindViolation(zones, @"D:\書類", new[] { (@"C:\a", 5000L) }, move: false));
    }

    [Fact]
    public void ZoneScope()
    {
        var zone = new QuotaZone(1, @"D:\Users\taro", 100, "user", Taro, null, 80);
        Assert.True(zone.AppliesTo(Taro, Standard));
        Assert.False(zone.AppliesTo(5, Standard));
        Assert.True(new QuotaZone(2, @"D:\", 100, "role", null, Guest, 80).AppliesTo(7, Guest));
    }
}
