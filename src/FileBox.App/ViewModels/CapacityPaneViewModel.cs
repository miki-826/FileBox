using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Services;
using FileBox.Contracts;

namespace FileBox.App.ViewModels;

/// <summary>容量分析・クォータ管理（04_画面設計 S06）。</summary>
public sealed partial class CapacityPaneViewModel : PaneViewModel
{
    public override PaneKind Kind => PaneKind.Capacity;

    [ObservableProperty]
    string _path;

    [ObservableProperty]
    UsageReport? _report;

    [ObservableProperty]
    List<ZoneDto> _zones = new();

    [ObservableProperty]
    bool _isLoading;

    [ObservableProperty]
    string? _errorText;

    /// <summary>管理者が見るときは全員分のゾーン、それ以外は自分に効くゾーン。</summary>
    public List<UserDto> Users { get; private set; } = new();
    public List<RoleDto> Roles { get; private set; } = new();

    public CapacityPaneViewModel(string? path, string? id = null)
    {
        if (id != null) Id = id;
        _path = path ?? "";
        Title = "容量";
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorText = null;
        try
        {
            var policy = PolicyService.Instance;
            if (policy.Session == null) await policy.RefreshAsync();   // 起動直後はまだアカウント情報が届いていない
            if (policy.IsAdmin)
            {
                var data = await IndexClient.Instance.CallAsync<AdminData>(PolicyMethods.AdminUsers);
                Zones = data?.Zones ?? new();
                Users = data?.Users ?? new();
                Roles = data?.Roles ?? new();
            }
            else
            {
                await policy.RefreshAsync();
                Zones = policy.Session?.Zones ?? new();
            }
            if (Path.Length == 0) Path = Zones.FirstOrDefault()?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Report = await IndexClient.Instance.CallAsync<UsageReport>(PolicyMethods.Usage, Path);
            if (Report == null) ErrorText = $"「{Path}」は索引にありません（索引の作成中か、対象外のドライブです）";
            Title = "容量: " + (System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsLoading = false; }
    }

    public Task OpenAsync(string path)
    {
        Path = path;
        return LoadAsync();
    }

    public override JsonObject Serialize() => new() { ["pane"] = "capacity", ["id"] = Id, ["path"] = Path };
}
