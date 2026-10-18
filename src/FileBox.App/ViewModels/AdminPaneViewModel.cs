using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Services;
using FileBox.Contracts;

namespace FileBox.App.ViewModels;

/// <summary>ユーザー・権限管理と監査ログ（04_画面設計 S07・S09、管理者のみ）。</summary>
public sealed partial class AdminPaneViewModel : PaneViewModel
{
    public override PaneKind Kind => PaneKind.Admin;

    [ObservableProperty]
    AdminData? _data;

    [ObservableProperty]
    List<AuditDto> _audit = new();

    [ObservableProperty]
    string? _errorText;

    public AuditQuery AuditFilter { get; } = new() { From = DateTime.Today.AddDays(-7), Limit = 2000 };

    public AdminPaneViewModel(string? id = null)
    {
        if (id != null) Id = id;
        Title = "管理";
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        ErrorText = null;
        try
        {
            Data = await IndexClient.Instance.CallAsync<AdminData>(PolicyMethods.AdminUsers);
            await LoadAuditAsync();
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    public async Task LoadAuditAsync()
    {
        try { Audit = await IndexClient.Instance.CallAsync<List<AuditDto>>(PolicyMethods.AdminAudit, AuditFilter) ?? new(); }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    /// <summary>本人確認のうえ管理操作を送り、一覧を読み直す。</summary>
    public async Task<bool> ChangeAsync(string reason, string method, object? args)
    {
        if (!await AdminVerifier.VerifyAsync(reason)) return false;
        try
        {
            await IndexClient.Instance.CallAsync<object>(method, args);
            await PolicyService.Instance.RefreshAsync();
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            App.Window?.ShowInfo(ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return false;
        }
    }

    public override JsonObject Serialize() => new() { ["pane"] = "admin", ["id"] = Id };
}
