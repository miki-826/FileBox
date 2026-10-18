using System.ComponentModel;
using System.Text;
using FileBox.App.Models;
using FileBox.App.ViewModels;
using FileBox.Contracts;
using FileBox.Core.Security;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 管理画面（04_画面設計 S07 ユーザー・権限、S09 監査ログ）。アカウントは Windows のサインインから自動登録されるので、
/// ここではロール・容量・フォルダ権限を割り当てるだけ。変更のたびに Windows Hello 等で本人確認する。
/// </summary>
public sealed class AdminPaneView : UserControl
{
    public AdminPaneViewModel ViewModel { get; }
    readonly StackPanel _users = new() { Spacing = 6 };
    readonly StackPanel _roles = new() { Spacing = 6 };
    readonly StackPanel _perms = new() { Spacing = 6 };
    readonly StackPanel _audit = new() { Spacing = 2 };
    readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 0) };

    static readonly (Permission Flag, string Name)[] Flags =
    {
        (Permission.Read, "閲覧"), (Permission.Create, "作成"), (Permission.Modify, "編集"), (Permission.Delete, "削除"), (Permission.Share, "リンク"),
    };

    public AdminPaneView(AdminPaneViewModel viewModel)
    {
        ViewModel = viewModel;
        // 見出しボタンで切り替える（Pivot は区画の高さいっぱいに広がらないため使わない）
        var pages = new (string Header, UIElement Content)[]
        {
            ("ユーザー", Scroll(_users)), ("ロール", Scroll(_roles)), ("フォルダの権限", Scroll(_perms)), ("監査ログ", AuditPanel()),
        };
        var host = new Grid();
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(12, 8, 12, 4) };
        var tabs = new List<ToggleButton>();
        foreach (var (header, content) in pages)
        {
            var tab = new ToggleButton { Content = header, Padding = new Thickness(14, 5, 14, 5) };
            tab.Click += (_, _) => Select(tabs.IndexOf(tab));
            tabs.Add(tab);
            strip.Children.Add(tab);
            content.Visibility = Visibility.Collapsed;
            host.Children.Add(content);
        }
        void Select(int index)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                tabs[i].IsChecked = i == index;
                pages[i].Content.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        Select(0);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(strip);
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);
        Grid.SetRow(host, 2);
        root.Children.Add(host);
        Content = root;
        ViewModel.PropertyChanged += OnChanged;
        Render();
    }

    static ScrollViewer Scroll(UIElement content) => new() { Content = content, Padding = new Thickness(4, 8, 12, 12) };

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdminPaneViewModel.Audit)) RenderAudit();
        else Render();
    }

    void Render()
    {
        _error.Text = ViewModel.ErrorText ?? "";
        _error.Visibility = ViewModel.ErrorText == null ? Visibility.Collapsed : Visibility.Visible;
        if (ViewModel.Data is not { } data) return;
        RenderUsers(data);
        RenderRoles(data);
        RenderPermissions(data);
        RenderAudit();
    }

    // ================================================================ ユーザー

    void RenderUsers(AdminData data)
    {
        _users.Children.Clear();
        _users.Children.Add(Note("Windows にサインインしている人が FileBox を初めて開くと自動で登録されます（ログイン画面はありません）。"));
        var defaultRole = RoleCombo(data, data.DefaultRoleId, "新しいアカウントの既定ロール");
        defaultRole.SelectionChanged += async (_, _) =>
        {
            if (defaultRole.SelectedItem is ComboBoxItem { Tag: long id } && id != data.DefaultRoleId)
                await ViewModel.ChangeAsync("既定ロールを変更", PolicyMethods.AdminSetDefaultRole, id);
        };
        _users.Children.Add(defaultRole);

        foreach (var u in data.Users)
        {
            var card = Card();
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = u.Account, FontWeight = FontWeights.SemiBold });
            name.Children.Add(Note($"使用量 {Format.HumanSize(u.UsedBytes)}・最終利用 {u.LastSeenAt:yyyy/MM/dd HH:mm}"));
            var os = new CheckBox { Content = "OS 強制モード", IsChecked = u.OsEnforced, IsEnabled = data.Elevated, Margin = new Thickness(0, 4, 0, 0) };
            ToolTipService.SetToolTip(os, data.Elevated
                ? "フォルダの「拒否」を Windows のアクセス権にも設定し、ドライブ全体のユーザー別容量を Windows のディスククォータにします。エクスプローラーからも迂回できなくなります。"
                : "Indexer を管理者として起動すると使えます（状態表示の「管理者として再起動」）");
            os.Click += async (_, _) =>
            {
                bool on = os.IsChecked == true;
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = on ? $"{u.Account} に OS 強制モードを設定しますか？" : $"{u.Account} の OS 強制モードを解除しますか？",
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = on
                            ? "このユーザーに効くフォルダの「拒否」を Windows のアクセス権（NTFS）にも設定します。エクスプローラーや他のアプリからも、その操作ができなくなります。\nFileBox が付けた設定だけを記録しておき、解除するとそれだけを外します。"
                            : "FileBox が Windows 側に付けたアクセス権とディスククォータを外します。",
                    },
                    PrimaryButtonText = on ? "設定する" : "解除する", CloseButtonText = "キャンセル",
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary ||
                    !await ViewModel.ChangeAsync("OS 強制モードを変更", PolicyMethods.AdminOsEnforce, new OsEnforceRequest { UserId = u.Id, Enabled = on }))
                    os.IsChecked = !on;
            };
            name.Children.Add(os);
            grid.Children.Add(name);
            var role = RoleCombo(data, u.RoleId, "ロール");
            Grid.SetColumn(role, 1);
            grid.Children.Add(role);
            var quota = new NumberBox
            {
                Header = "容量上限（GB・空欄=ロール既定）", Value = u.QuotaBytes is long q ? Math.Round(q / 1073741824.0, 2) : double.NaN,
                Minimum = 0, Width = 200, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            Grid.SetColumn(quota, 2);
            grid.Children.Add(quota);
            var disabled = new CheckBox { Content = "利用停止（閲覧のみ）", IsChecked = u.IsDisabled, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(disabled, 3);
            grid.Children.Add(disabled);
            var save = new Button { Content = "保存", VerticalAlignment = VerticalAlignment.Bottom, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            save.Click += async (_, _) => await ViewModel.ChangeAsync($"{u.Account} の設定を変更", PolicyMethods.AdminUpdateUser, new UserDto
            {
                Id = u.Id, Account = u.Account, RoleId = (role.SelectedItem as ComboBoxItem)?.Tag as long? ?? u.RoleId,
                QuotaBytes = double.IsNaN(quota.Value) ? null : (long)(quota.Value * 1073741824.0), IsDisabled = disabled.IsChecked == true,
            });
            Grid.SetColumn(save, 4);
            grid.Children.Add(save);
            card.Child = grid;
            _users.Children.Add(card);
        }
    }

    static ComboBox RoleCombo(AdminData data, long selected, string header)
    {
        var combo = new ComboBox { Header = header, MinWidth = 140 };
        foreach (var r in data.Roles) combo.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r.Id });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (long)i.Tag == selected);
        return combo;
    }

    // ================================================================ ロール

    void RenderRoles(AdminData data)
    {
        _roles.Children.Clear();
        _roles.Children.Add(Note("ロールの既定の権限は、フォルダごとの設定が無い場所に適用されます。管理者ロールは常にすべて可能です。"));
        foreach (var r in data.Roles)
        {
            var card = Card();
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold });
            var checks = FlagChecks((Permission)r.DefaultFlags, enabled: r.Id != 1);
            panel.Children.Add(checks.Panel);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var quota = new NumberBox
            {
                Header = "既定の容量上限（GB・空欄=無制限）", Value = r.QuotaBytes is long q ? Math.Round(q / 1073741824.0, 2) : double.NaN,
                Minimum = 0, Width = 220, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            row.Children.Add(quota);
            var save = new Button { Content = "保存", VerticalAlignment = VerticalAlignment.Bottom };
            save.Click += async (_, _) => await ViewModel.ChangeAsync($"ロール「{r.Name}」を変更", PolicyMethods.AdminUpdateRole, new RoleDto
            {
                Id = r.Id, Name = r.Name, DefaultFlags = (int)(checks.Value() | ((Permission)r.DefaultFlags & Permission.Admin)),
                QuotaBytes = double.IsNaN(quota.Value) ? null : (long)(quota.Value * 1073741824.0),
            });
            row.Children.Add(save);
            panel.Children.Add(row);
            card.Child = panel;
            _roles.Children.Add(card);
        }
    }

    // ================================================================ フォルダ権限

    void RenderPermissions(AdminData data)
    {
        _perms.Children.Clear();
        _perms.Children.Add(Note("設定したフォルダと、その中のすべてに効きます。より深いフォルダの設定が優先され、同じ場所の「拒否」は「許可」より優先されます。" +
                                 "FileBox からの操作だけに効きます（エクスプローラーからは迂回できます）。"));

        // ---- 追加フォーム
        var form = Card();
        var fp = new StackPanel { Spacing = 8 };
        fp.Children.Add(new TextBlock { Text = "権限を設定", FontWeight = FontWeights.SemiBold });
        var path = new TextBox { Header = "フォルダ", PlaceholderText = @"D:\書類", MinWidth = 360 };
        var browse = new Button { Content = "参照…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) => { if (await SuggestMoveDialog.PickFolderAsync() is { } p) path.Text = p; };
        var subject = new ComboBox { Header = "対象", MinWidth = 200 };
        foreach (var r in data.Roles) subject.Items.Add(new ComboBoxItem { Content = $"ロール: {r.Name}", Tag = ("role", r.Id) });
        foreach (var u in data.Users) subject.Items.Add(new ComboBoxItem { Content = $"ユーザー: {u.Account}", Tag = ("user", u.Id) });
        subject.SelectedIndex = Math.Min(1, subject.Items.Count - 1);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(path);
        top.Children.Add(browse);
        top.Children.Add(subject);
        fp.Children.Add(top);
        var allow = FlagChecks(Permission.None, true);
        var deny = FlagChecks(Permission.None, true);
        fp.Children.Add(Labeled("許可", allow.Panel));
        fp.Children.Add(Labeled("拒否", deny.Panel));
        var add = new Button { Content = "設定する", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += async (_, _) =>
        {
            if (path.Text.Trim().Length < 3 || subject.SelectedItem is not ComboBoxItem { Tag: ValueTuple<string, long> who }) return;
            await ViewModel.ChangeAsync("フォルダの権限を変更", PolicyMethods.AdminSetPermission, new PermissionDto
            {
                Path = path.Text.Trim(), Allow = (int)allow.Value(), Deny = (int)deny.Value(),
                UserId = who.Item1 == "user" ? who.Item2 : null, RoleId = who.Item1 == "role" ? who.Item2 : null,
            });
        };
        fp.Children.Add(add);
        form.Child = fp;
        _perms.Children.Add(form);

        // ---- 一覧
        foreach (var p in data.Permissions)
        {
            string who = p.UserId is long uid ? "ユーザー: " + (data.Users.FirstOrDefault(u => u.Id == uid)?.Account ?? uid.ToString())
                                              : "ロール: " + (data.Roles.FirstOrDefault(r => r.Id == p.RoleId)?.Name ?? p.RoleId?.ToString());
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 4, 8, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = p.Path, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Note($"{who}　許可: {PolicyEvaluator.Describe((Permission)p.Allow)}　拒否: {PolicyEvaluator.Describe((Permission)p.Deny)}"));
            grid.Children.Add(text);
            var del = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(del, "この設定を削除");
            del.Click += async (_, _) => await ViewModel.ChangeAsync("フォルダの権限を削除", PolicyMethods.AdminDeletePermission, p.Id);
            Grid.SetColumn(del, 1);
            grid.Children.Add(del);
            _perms.Children.Add(grid);
        }
    }

    static (StackPanel Panel, Func<Permission> Value) FlagChecks(Permission initial, bool enabled)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var boxes = new List<(CheckBox Box, Permission Flag)>();
        foreach (var (flag, name) in Flags)
        {
            var box = new CheckBox { Content = name, IsChecked = initial.HasFlag(flag), IsEnabled = enabled, MinWidth = 76 };
            boxes.Add((box, flag));
            panel.Children.Add(box);
        }
        return (panel, () => boxes.Where(b => b.Box.IsChecked == true).Aggregate(Permission.None, (acc, b) => acc | b.Flag));
    }

    static UIElement Labeled(string label, UIElement content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = label, Width = 40, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(content);
        return row;
    }

    // ================================================================ 監査ログ

    UIElement AuditPanel()
    {
        var root = new Grid { Padding = new Thickness(4, 8, 12, 12), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var period = new ComboBox { Header = "期間", MinWidth = 120 };
        foreach (var (label, days) in new[] { ("今日", 0), ("7日間", 7), ("30日間", 30), ("すべて", -1) })
            period.Items.Add(new ComboBoxItem { Content = label, Tag = days });
        period.SelectedIndex = 1;
        var action = new ComboBox { Header = "操作", MinWidth = 160 };
        foreach (var (label, value) in new[]
        {
            ("すべて", ""), ("保存・作成", "save"), ("移動", "move"), ("コピー", "copy"), ("削除", "delete"), ("名前の変更", "rename"),
            ("リンクで開く", "link_open"), ("リンク発行", "link_create"), ("初回利用", "user_register"), ("本人確認", "admin_verify"),
            ("権限の変更", "perm_change"), ("容量の変更", "quota_change"),
        })
            action.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        action.SelectedIndex = 0;
        var apply = new Button { Content = "表示", VerticalAlignment = VerticalAlignment.Bottom };
        apply.Click += async (_, _) =>
        {
            int days = (int)((ComboBoxItem)period.SelectedItem).Tag;
            ViewModel.AuditFilter.From = days < 0 ? null : DateTime.Today.AddDays(-days);
            string a = (string)((ComboBoxItem)action.SelectedItem).Tag;
            ViewModel.AuditFilter.Action = a.Length == 0 ? null : a;
            await ViewModel.LoadAuditAsync();
        };
        var export = new Button { Content = "CSV で保存…", VerticalAlignment = VerticalAlignment.Bottom };
        export.Click += async (_, _) => await ExportCsvAsync();
        filters.Children.Add(period);
        filters.Children.Add(action);
        filters.Children.Add(apply);
        filters.Children.Add(export);
        root.Children.Add(filters);
        var scroll = new ScrollViewer { Content = _audit };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    void RenderAudit()
    {
        _audit.Children.Clear();
        if (ViewModel.Audit.Count == 0) { _audit.Children.Add(Note("記録はありません")); return; }
        foreach (var a in ViewModel.Audit.Take(500))
        {
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Cell(a.At.ToString("MM/dd HH:mm:ss"), 0));
            grid.Children.Add(Cell(a.Account ?? "-", 1));
            var result = Cell($"{a.Action}{(a.Result == "ok" ? "" : $"（{a.Result}）")}", 2);
            if (a.Result != "ok") result.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            grid.Children.Add(result);
            string detail = string.Join("  ", new[] { a.Src, a.Dst is { } d ? "→ " + d : null, a.Detail }.Where(x => !string.IsNullOrEmpty(x)));
            grid.Children.Add(Cell(detail, 3));
            _audit.Children.Add(grid);
        }
        if (ViewModel.Audit.Count > 500) _audit.Children.Add(Note($"ほか {ViewModel.Audit.Count - 500} 件（CSV で全件を保存できます）"));
    }

    static TextBlock Cell(string text, int column)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
        ToolTipService.SetToolTip(t, text);
        Grid.SetColumn(t, column);
        return t;
    }

    async Task ExportCsvAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"FileBox監査ログ_{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        if (App.Window != null) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("日時,アカウント,操作,元,先,バイト,結果,詳細\r\n");
        foreach (var a in ViewModel.Audit)
            sb.Append(string.Join(",", Q(a.At.ToString("yyyy-MM-dd HH:mm:ss")), Q(a.Account), Q(a.Action), Q(a.Src), Q(a.Dst), Q(a.Bytes?.ToString()), Q(a.Result), Q(a.Detail))).Append("\r\n");
        // Excel で文字化けしないよう BOM 付き UTF-8
        await File.WriteAllTextAsync(file.Path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        App.Window?.ShowInfo($"{ViewModel.Audit.Count} 件を保存しました");
    }

    // ================================================================ 部品

    static Border Card() => new()
    {
        Padding = new Thickness(12, 8, 12, 10), CornerRadius = new CornerRadius(6),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
    };

    static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
