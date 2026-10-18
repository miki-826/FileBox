using FileBox.App.Models;
using FileBox.App.Services;
using FileBox.App.ViewModels;
using Microsoft.UI.Xaml;

namespace FileBox.App;

/// <summary>アカウント・容量の表示と、容量分析・管理画面を開く（02_機能設計 §5.3・§6）。</summary>
public sealed partial class MainWindow
{
    void InitializePolicy()
    {
        PolicyService.Instance.Changed += UpdatePolicyUi;
        PolicyService.Instance.Start(DispatcherQueue);
        UpdatePolicyUi();
    }

    void UpdatePolicyUi()
    {
        var policy = PolicyService.Instance;
        var s = policy.Session;
        AccountText.Text = s == null ? "" : s.Unmanaged ? s.Account : $"{s.Account}（{s.RoleName}）";
        AdminButton.Visibility = policy.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        ZoneList.ItemsSource = (s?.Zones ?? new()).Select(z => new SidebarItem
        {
            Name = Path.GetFileName(z.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : z.Path,
            Glyph = "",
            Path = z.Path,
            Kind = SidebarKind.Zone,
            Usage = z.LimitBytes > 0 && z.UsedBytes >= 0 ? Math.Min(1, (double)z.UsedBytes / z.LimitBytes) : 0,
            Detail = z.UsedBytes < 0 ? "使用量を集計できません" : $"{Format.HumanSize(z.UsedBytes)} / {Format.HumanSize(z.LimitBytes)}",
        }).ToList();
    }

    /// <summary>容量分析を開く（すでにあればその場所を表示）。</summary>
    public void OpenCapacity(string? path = null)
    {
        foreach (var tab in _tabs)
        {
            if (tab.Model.Panes.OfType<CapacityPaneViewModel>().FirstOrDefault() is { } pane)
            {
                Tabs.SelectedItem = tab.Item;
                tab.Model.ActivePane = pane;
                if (path != null) _ = pane.OpenAsync(path);
                return;
            }
        }
        AddTab(new TabViewModel(new CapacityPaneViewModel(path)));
    }

    public async void OpenAdmin()
    {
        if (PolicyService.Instance.Session == null) await PolicyService.Instance.RefreshAsync();   // 起動直後は確認が済むのを待つ
        if (!PolicyService.Instance.IsAdmin) { ShowInfo("管理者ロールのアカウントだけが使えます"); return; }
        foreach (var tab in _tabs)
        {
            if (tab.Model.Panes.OfType<AdminPaneViewModel>().FirstOrDefault() is { } pane)
            {
                Tabs.SelectedItem = tab.Item;
                tab.Model.ActivePane = pane;
                _ = pane.LoadAsync();
                return;
            }
        }
        AddTab(new TabViewModel(new AdminPaneViewModel()));
    }

    void Capacity_Click(object sender, RoutedEventArgs e) => OpenCapacity(ActiveFolder is { IsPc: false } f ? f.CurrentPath : null);

    void Admin_Click(object sender, RoutedEventArgs e) => OpenAdmin();
}
