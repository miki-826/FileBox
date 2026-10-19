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
    public List<DriveUsage> Drives { get; private set; } = new();
    CancellationTokenSource? _loadCts;

    public CapacityPaneViewModel(string? path, string? id = null)
    {
        if (id != null) Id = id;
        _path = path ?? "";
        Title = "容量";
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorText = null;
        Report = null;
        string path = Path.Length == 0 ? System.IO.Path.GetPathRoot(Environment.SystemDirectory)! : Path;
        Path = path;
        try
        {
            Drives = await Task.Run(() => DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new DriveUsage(
                d.RootDirectory.FullName, string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})",
                d.TotalSize, d.TotalSize - d.TotalFreeSpace, d.TotalFreeSpace)).ToList(), cts.Token);
            var report = await FolderUsageService.ReadAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;
            Report = report;
            Title = "容量: " + (System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path);
            // 上限の設定を取得できなくても、使用量の表示は続ける。
            var policy = PolicyService.Instance;
            try
            {
                if (policy.Session == null) await policy.RefreshAsync();
                if (cts.IsCancellationRequested) return;
                if (policy.IsAdmin)
                {
                    var data = await IndexClient.Instance.CallAsync<AdminData>(PolicyMethods.AdminUsers, ct: cts.Token);
                    if (cts.IsCancellationRequested) return;
                    Users = data?.Users ?? new();
                    Roles = data?.Roles ?? new();
                    Zones = data?.Zones ?? new();
                }
                else Zones = policy.Session?.Zones ?? new();
            }
            catch (InvalidOperationException) { Zones = new(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) ErrorText = ex.Message; }
        finally { if (_loadCts == cts) IsLoading = false; }
    }

    public Task OpenAsync(string path)
    {
        Path = path;
        return LoadAsync();
    }

    /// <summary>集計を途中でやめる（ここまでの結果は残らない。「更新」でやり直せる）。</summary>
    public void Cancel()
    {
        _loadCts?.Cancel();
        IsLoading = false;
        ErrorText ??= "集計を中止しました。「更新」でやり直せます。";
    }

    public override void Dispose() => _loadCts?.Cancel();

    public override JsonObject Serialize() => new() { ["pane"] = "capacity", ["id"] = Id, ["path"] = Path };
}

public sealed record DriveUsage(string Path, string Name, long Total, long Used, long Free);
