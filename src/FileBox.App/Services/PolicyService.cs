using FileBox.Contracts;
using FileBox.Core.Security;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Services;

/// <summary>
/// App 側の権限・容量の窓口（02_機能設計 §5・§6）。判定材料（自分の権限設定・ゾーンの使用量）は Indexer から受け取り、
/// FileBox 経由の操作の前に確認する。Indexer に繋がらない・system.db が無いときは「管理なし」で全操作を許可する。
/// </summary>
public sealed class PolicyService
{
    public static PolicyService Instance { get; } = new();

    SessionInfo? _session;
    DispatcherQueueTimer? _timer;
    readonly HashSet<(long Zone, int Level)> _warned = new();

    public event Action? Changed;

    public SessionInfo? Session => _session;
    public bool Managed => _session is { Unmanaged: false };
    public bool IsAdmin => _session?.IsAdmin == true;

    public void Start(DispatcherQueue ui)
    {
        _ = RefreshAsync();
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var session = await IndexClient.Instance.CallAsync<SessionInfo>(PolicyMethods.Session);
            bool first = _session == null;
            _session = session;
            if (first && session?.FirstUse == true && session.Managed())
                App.Window?.ShowInfo($"ようこそ。{session.Account} を「{session.RoleName}」として登録しました");
            WarnZones();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        Changed?.Invoke();
    }

    // ================================================================ 権限

    public Permission Effective(string path)
    {
        if (!Managed) return Permission.All;
        var s = _session!;
        var rules = s.Permissions.Select(p => new PermissionRule(p.Path, (Permission)p.Allow, (Permission)p.Deny, p.UserId, p.RoleId)).ToList();
        return PolicyEvaluator.Effective(path, s.UserId, s.RoleId, (Permission)s.RoleDefault, rules);
    }

    public bool Can(Permission permission, string path) => Effective(path).HasFlag(permission);

    static string ActionName(Permission p) => p switch
    {
        Permission.Read => "閲覧",
        Permission.Create => "作成・保存",
        Permission.Modify => "名前の変更・編集",
        Permission.Delete => "削除",
        Permission.Share => "リンクの発行",
        _ => "この操作",
    };

    /// <summary>許可されていなければ理由を表示して監査ログに残し、false を返す。</summary>
    public bool Demand(Permission permission, string path, string action)
    {
        if (Can(permission, path)) return true;
        App.Window?.ShowInfo($"「{Leaf(path)}」での{ActionName(permission)}は許可されていません（{_session?.RoleName}）", InfoBarSeverity.Warning);
        Audit(action, path, null, null, "denied", $"{ActionName(permission)}の権限なし");
        return false;
    }

    // ================================================================ 容量（§5.2）

    /// <summary>
    /// destination へのコピー/移動が容量の上限を超えないか。超えるなら理由と空け方を案内して false。
    /// </summary>
    public async Task<bool> EnsureCapacityAsync(IReadOnlyList<string> sources, string destination, bool move, string action)
    {
        if (!Managed) return true;
        var s = _session!;
        var sizes = await Task.Run(() => sources.Select(p => (p, SizeOf(p))).ToList());
        var zones = s.Zones.Where(z => z.UsedBytes >= 0)
            .Select(z => new QuotaZone(z.Id, z.Path, z.LimitBytes, z.Scope, z.UserId, z.RoleId, z.WarnPercent, z.UsedBytes));
        var violation = QuotaCheck.FindViolation(zones, destination, sizes, move);
        long incoming = sizes.Sum(x => x.Item2);
        if (violation != null)
        {
            Audit(action, sources.FirstOrDefault(), destination, incoming, "quota_exceeded", violation.Path);
            await ShowQuotaDialogAsync($"「{violation.Path}」の容量上限を超えるため保存できません",
                $"上限 {Models.Format.HumanSize(violation.LimitBytes)} のうち {Models.Format.HumanSize(violation.UsedBytes)} 使用中で、"
                + $"残り {Models.Format.HumanSize(violation.Remaining)} です（今回 {Models.Format.HumanSize(incoming)}）。", violation.Path);
            return false;
        }
        // ユーザー別容量: FileBox 経由で新しく増えるのはコピーと、別のドライブからの移動
        if (s.UserQuotaBytes is long limit && !move && s.UserUsedBytes + incoming > limit)
        {
            Audit(action, sources.FirstOrDefault(), destination, incoming, "quota_exceeded", "ユーザー別容量");
            await ShowQuotaDialogAsync("あなたの容量の上限を超えるため保存できません",
                $"{s.Account} の上限 {Models.Format.HumanSize(limit)} のうち {Models.Format.HumanSize(s.UserUsedBytes)} 使用中です（今回 {Models.Format.HumanSize(incoming)}）。", null);
            return false;
        }
        return true;
    }

    static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length);
        }
        catch (Exception) { return 0; }
    }

    static async Task ShowQuotaDialogAsync(string title, string message, string? zonePath)
    {
        if (App.Window?.Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message + "\n\n大きいファイルや古いファイルを整理すると空きを作れます。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "閉じる",
            PrimaryButtonText = zonePath != null ? "容量の内訳を見る" : "",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && zonePath != null) App.Window?.OpenCapacity(zonePath);
    }

    /// <summary>外部アプリの書き込みも含めた使用量を見て、警告ライン・上限を超えたら一度だけ通知する（§5.2）。</summary>
    void WarnZones()
    {
        if (!Managed) return;
        foreach (var z in _session!.Zones)
        {
            if (z.UsedBytes < 0 || z.LimitBytes <= 0) continue;
            double ratio = (double)z.UsedBytes / z.LimitBytes;
            int level = ratio >= 1 ? 100 : ratio * 100 >= z.WarnPercent ? z.WarnPercent : 0;
            if (level == 0) { _warned.RemoveWhere(w => w.Zone == z.Id); continue; }
            if (!_warned.Add((z.Id, level))) continue;
            App.Window?.ShowInfo(level >= 100
                    ? $"「{Leaf(z.Path)}」が容量の上限を超えています（{ratio:P0}）。不要なファイルを整理してください"
                    : $"「{Leaf(z.Path)}」の使用量が {ratio:P0} になりました（上限 {Models.Format.HumanSize(z.LimitBytes)}）",
                level >= 100 ? InfoBarSeverity.Error : InfoBarSeverity.Warning, "内訳を見る", () => App.Window?.OpenCapacity(z.Path));
        }
    }

    // ================================================================ 記録

    /// <summary>監査ログへ（失敗しても操作は止めない）。</summary>
    public void Audit(string action, string? src, string? dst, long? bytes, string result = "ok", string? detail = null)
    {
        if (!Managed) return;
        _ = Fire(() => IndexClient.Instance.CallAsync<object>(PolicyMethods.Audit,
            new AuditDto { Action = action, Src = src, Dst = dst, Bytes = bytes, Result = result, Detail = detail }));
    }

    /// <summary>FileBox 経由で作ったファイルの作成者を記録（ユーザー別容量）。</summary>
    public void RecordOwners(IReadOnlyList<string> paths)
    {
        if (!Managed || _session!.UserQuotaBytes == null || paths.Count == 0) return;
        _ = Fire(() => IndexClient.Instance.CallAsync<object>(PolicyMethods.RecordOwners, paths.ToList()));
    }

    static async Task Fire(Func<Task> call)
    {
        try { await call(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
}

static class SessionExtensions
{
    public static bool Managed(this SessionInfo s) => !s.Unmanaged;
}
