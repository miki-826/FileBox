using FileBox.App.Services;
using FileBox.App.ViewModels;
using FileBox.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Views;

/// <summary>
/// 設定（04_画面設計 設定）。エクスプローラー置き換え・保存先の提案・検索・リンク・表示・データ・FileBox が変更した Windows の設定。
/// ダイアログの中でダイアログは開けないため、一覧や管理はすべてこの中の開閉式の欄で行う。
/// </summary>
public static class SettingsDialog
{
    public static UserStore? Store { get; set; }

    public static async Task ShowAsync(XamlRoot root)
    {
        var store = Store ?? throw new InvalidOperationException("SettingsDialog.Store が未設定");
        var explorer = ExplorerIntegration.Instance;
        var links = LinkService.Instance;
        var panel = new StackPanel { Spacing = 16, MinWidth = 480, MaxWidth = 500, Padding = new Thickness(0, 0, 12, 12) };
        bool restartNeeded = false;
        var restartNote = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = "言語・アクセント色・キーの割り当ては、FileBox を起動し直すと反映されます。" };
        void NeedRestart() { restartNeeded = true; restartNote.IsOpen = true; }

        // ---- エクスプローラー置き換え（02_機能設計 §7）
        panel.Children.Add(Header("エクスプローラーの代わりに使う"));
        var replace = Toggle("フォルダやドライブを開くときに FileBox を使う", explorer.IsEnabled && explorer.IsActiveInRegistry(),
            "デスクトップやスタートメニューからフォルダを開くと FileBox が開きます。オフにすると元のエクスプローラーに戻ります（変更は現在のユーザーだけ）。");
        replace.Toggled += (_, _) =>
        {
            try
            {
                if (replace.IsOn) explorer.Enable(); else explorer.Disable();
            }
            catch (Exception ex) { App.ShowError("設定を変更できませんでした", ex); }
        };
        panel.Children.Add(replace);
        var winE = Toggle("Win + E で FileBox を開く", explorer.WinEEnabled, "FileBox が起動している間だけ有効です。");
        winE.Toggled += (_, _) => explorer.SetWinE(winE.IsOn);
        panel.Children.Add(winE);
        panel.Children.Add(ConversionSettings.Build(store));

        // ---- 保存先の提案（§1）
        panel.Children.Add(Header("保存先の提案"));
        var saveDialog = Toggle("他のアプリの「名前を付けて保存」で保存先を提案する", SaveDialogWatcher.Instance.Enabled,
            "メモ帳・Office・ブラウザなどの標準の保存画面の横に候補を表示します。選ぶとファイル名欄に場所が入ります。");
        saveDialog.Toggled += (_, _) => SaveDialogWatcher.Instance.SetEnabled(saveDialog.IsOn);
        panel.Children.Add(saveDialog);
        panel.Children.Add(LearnedSection(store));

        // ---- 検索
        panel.Children.Add(Header("検索"));
        panel.Children.Add(ContentSection());
        var hotkey = GlobalHotkey.Instance;
        var hotkeyToggle = Toggle("ホットキーでどこからでも検索する", hotkey.Enabled, "ほかのアプリを使っているときも、キーを押すと FileBox の検索欄が開きます（FileBox が起動している間だけ）。");
        var hotkeyChoice = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var c in GlobalHotkey.Choices) hotkeyChoice.Items.Add(c.Label);
        hotkeyChoice.SelectedIndex = hotkey.ChoiceIndex;
        void ApplyHotkey()
        {
            if (!hotkey.Set(hotkeyToggle.IsOn, hotkeyChoice.SelectedIndex))
                App.Window?.ShowInfo($"{GlobalHotkey.Choices[hotkeyChoice.SelectedIndex].Label} はほかのアプリが使っています。別のキーを選んでください", InfoBarSeverity.Warning);
        }
        hotkeyToggle.Toggled += (_, _) => ApplyHotkey();
        hotkeyChoice.SelectionChanged += (_, _) => { if (hotkeyToggle.IsOn) ApplyHotkey(); };
        panel.Children.Add(hotkeyToggle);
        panel.Children.Add(hotkeyChoice);

        // ---- リンク（§4）
        panel.Children.Add(Header("リンク"));
        var http = Toggle("カレンダー用の http リンクを使う", links.HttpLinksEnabled,
            $"Google カレンダーなど filebox:// をリンクにしないアプリ向けです。このパソコンの FileBox が起動中のときだけ開けます（127.0.0.1:{links.HttpPort}）。");
        http.Toggled += (_, _) =>
        {
            store.SetSetting("links.http", http.IsOn ? "1" : "0");
            App.Window?.RestartLinkServer();
        };
        panel.Children.Add(http);
        var unc = Toggle("ネットワーク上のフォルダ（\\\\サーバー\\共有）へのリンクを開く", links.AllowUnc, "オフのときは、ネットワークパスを含むリンクを開きません。");
        unc.Toggled += (_, _) => store.SetSetting("links.allowUnc", unc.IsOn ? "1" : "0");
        panel.Children.Add(unc);

        // ---- 表示
        panel.Children.Add(Header("表示"));
        var theme = new ComboBox { Header = "テーマ", MinWidth = 240 };
        foreach (var t in ThemeService.Themes) theme.Items.Add(t.Label);
        theme.SelectedIndex = Array.FindIndex(ThemeService.Themes, t => t.Theme == ThemeService.Theme(store));
        theme.SelectionChanged += (_, _) => ThemeService.SetTheme(store, ThemeService.Themes[theme.SelectedIndex].Theme);
        var accent = new ComboBox { Header = "アクセント色（次の起動から）", MinWidth = 200 };
        accent.Items.Add("Windows の設定に合わせる");
        foreach (var (name, _) in ViewPreferences.Palette) accent.Items.Add(name);
        string? currentAccent = ThemeService.Accent(store);
        accent.SelectedIndex = currentAccent == null ? 0 : Array.FindIndex(ViewPreferences.Palette, p => p.Hex == currentAccent) + 1;
        accent.SelectionChanged += (_, _) =>
        {
            ThemeService.SetAccent(store, accent.SelectedIndex <= 0 ? null : ViewPreferences.Palette[accent.SelectedIndex - 1].Hex);
            NeedRestart();
        };
        var language = new ComboBox { Header = "言語 / Language（次の起動から）", MinWidth = 200 };
        foreach (var (_, label) in Text.Languages) language.Items.Add(label);
        language.SelectedIndex = Array.FindIndex(Text.Languages, l => l.Code == Text.Language(store));
        language.SelectionChanged += (_, _) =>
        {
            Text.SetLanguage(store, Text.Languages[language.SelectedIndex].Code);
            NeedRestart();
        };
        var looks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        looks.Children.Add(theme);
        looks.Children.Add(accent);
        panel.Children.Add(looks);
        panel.Children.Add(language);
        var restore = Toggle("起動時に前回のタブを復元する", store.GetSetting("restoreSession") != "0", "タブの構成は数秒ごとに保存するので、急に終了しても戻せます。");
        restore.Toggled += (_, _) => store.SetSetting("restoreSession", restore.IsOn ? "1" : "0");
        panel.Children.Add(restore);
        var hidden = Toggle("隠しファイルを表示する（Ctrl+H）", FolderPaneViewModel.ShowHidden);
        hidden.Toggled += (_, _) =>
        {
            FolderPaneViewModel.ShowHidden = hidden.IsOn;
            store.SetSetting("showHidden", hidden.IsOn ? "1" : "0");
        };
        panel.Children.Add(hidden);
        var checkboxes = Toggle("チェックボックスで項目を選ぶ", ViewPreferences.CheckBoxes, "タッチやマウスだけで操作するとき向けです。項目をクリックすると選択が切り替わります（開くのはダブルクリック）。");
        checkboxes.Toggled += (_, _) => ViewPreferences.SetCheckBoxes(checkboxes.IsOn);
        panel.Children.Add(checkboxes);
        var keys = new HyperlinkButton { Content = "ショートカット一覧とキーの割り当て（F1）" };
        keys.Click += (_, _) => { _pendingShortcuts = true; _current?.Hide(); };
        var tour = new HyperlinkButton { Content = "使い方の案内をもう一度見る" };
        tour.Click += (_, _) => { store.SetSetting("onboarding.done", "0"); _pendingTour = true; _current?.Hide(); };
        var helpRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        helpRow.Children.Add(keys);
        helpRow.Children.Add(tour);
        panel.Children.Add(helpRow);

        // ---- データ・更新・報告
        panel.Children.Add(Header("データと更新"));
        var update = Toggle("新しい preview 版が出たら知らせる", UpdateChecker.Enabled(store), "1 日 1 回、GitHub の配布ページの更新日時を確かめます（こちらの情報は送りません）。");
        update.Toggled += (_, _) => UpdateChecker.SetEnabled(store, update.IsOn);
        panel.Children.Add(update);
        var dataRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        dataRow.Children.Add(ActionButton("", "今すぐ確認", async () =>
        {
            var published = await UpdateChecker.CheckAsync(store, force: true);
            App.Window?.ShowInfo(published is DateTime p ? $"新しい preview 版があります（{p:yyyy/MM/dd HH:mm} 公開）" : "お使いの版が最新です（または確認できませんでした）",
                InfoBarSeverity.Informational, published != null ? "配布ページを開く" : null,
                () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateChecker.ReleasePage)));
        }));
        dataRow.Children.Add(ActionButton("", "設定を書き出す…", async () =>
        {
            if (await SettingsTransfer.ExportAsync(store) is { } path) App.Window?.ShowInfo($"設定を書き出しました: {Path.GetFileName(path)}", InfoBarSeverity.Success);
        }));
        dataRow.Children.Add(ActionButton("", "設定を取り込む…", async () =>
        {
            var result = await SettingsTransfer.ImportAsync(store);
            if (result is { Error: { } error }) App.Window?.ShowInfo(error, InfoBarSeverity.Warning);
            else if (result is { Count: var n }) { App.Window?.ShowInfo($"{n} 件の設定を取り込みました。表示の設定は次の起動から反映されます", InfoBarSeverity.Success); NeedRestart(); }
        }));
        dataRow.Children.Add(ActionButton("", "不具合を報告…", () => _ = App.Window?.ReportBugAsync()));
        panel.Children.Add(dataRow);
        panel.Children.Add(Note("書き出す設定には、Windows を変える設定（エクスプローラーの置き換えなど）とリンクの鍵は含めません。"));

        // ---- FileBox が変更した Windows の設定（元に戻せる）
        panel.Children.Add(Header("FileBox が変更した Windows の設定"));
        panel.Children.Add(ChangesSection(store, () => { replace.IsOn = explorer.IsEnabled && explorer.IsActiveInRegistry(); winE.IsOn = explorer.WinEEnabled; hotkeyToggle.IsOn = hotkey.Enabled; }));
        panel.Children.Insert(0, restartNote);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "設定",
            Content = new ScrollViewer { Content = panel, MaxHeight = 640 },
            CloseButtonText = "閉じる",
        };
        _current = dialog;
        _pendingShortcuts = _pendingTour = false;
        await dialog.ShowAsync();
        _current = null;
        if (_pendingShortcuts) await ShortcutsDialog.ShowAsync(root);
        if (_pendingTour) App.Window?.ShowOnboardingAgain();
        if (restartNeeded) App.Window?.ShowInfo("言語・アクセント色・キーの割り当ては、FileBox を起動し直すと反映されます");
    }

    static ContentDialog? _current;
    static bool _pendingShortcuts, _pendingTour;

    // ================================================================ 学習した保存先（02_機能設計 §1.5）

    static Expander LearnedSection(UserStore store)
    {
        var body = new StackPanel { Spacing = 6 };
        void Render()
        {
            body.Children.Clear();
            body.Children.Add(Note("選んだ保存先を覚えて、似た名前のファイルの候補に出します。間違って覚えたものは忘れさせ、候補に出したくない場所は外せます。"));
            var learned = store.GetLearnedFolders();
            if (learned.Count == 0) body.Children.Add(Note("まだ覚えた保存先はありません。"));
            foreach (var f in learned.Take(100))
            {
                var row = Row(f.FolderPath, $"{f.Count} 回・最後 {f.LastChosen:yyyy/MM/dd}");
                row.Buttons.Children.Add(SmallButton("", "忘れる", () => { store.ForgetLearnedFolder(f.FolderPath); Render(); }));
                row.Buttons.Children.Add(SmallButton("", "候補に出さない", () => { store.SetSuggestExcluded(f.FolderPath, true); store.ForgetLearnedFolder(f.FolderPath); SuggestionService.Instance.ReloadExclusions(); Render(); }));
                body.Children.Add(row.Grid);
            }
            var excluded = store.GetSuggestExcluded();
            if (excluded.Count > 0) body.Children.Add(new TextBlock { Text = "候補に出さない場所", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var path in excluded)
            {
                var row = Row(path, "配下も候補に出しません");
                row.Buttons.Children.Add(SmallButton("", "候補に戻す", () => { store.SetSuggestExcluded(path, false); SuggestionService.Instance.ReloadExclusions(); Render(); }));
                body.Children.Add(row.Grid);
            }
            var add = ActionButton("", "候補に出さない場所を追加…", async () =>
            {
                if (await SuggestMoveDialog.PickFolderAsync() is { } p) { store.SetSuggestExcluded(p, true); SuggestionService.Instance.ReloadExclusions(); Render(); }
            });
            var forgetAll = ActionButton("", "覚えたことをすべて忘れる", () => { store.ForgetAllLearned(); Render(); });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(add);
            if (learned.Count > 0) actions.Children.Add(forgetAll);
            body.Children.Add(actions);
        }
        Render();
        return Section("学習した保存先を管理", body);
    }

    // ================================================================ 本文検索（既定はオフ）

    static Expander ContentSection()
    {
        var indexer = ContentIndexer.Instance;
        var body = new StackPanel { Spacing = 6 };
        var status = new TextBlock { FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
        void UpdateStatus() => status.Text = indexer.Enabled ? (indexer.Status.Length > 0 ? indexer.Status : "準備中…") : "オフ";
        indexer.StatusChanged += () => status.DispatcherQueue?.TryEnqueue(UpdateStatus);
        var toggle = Toggle("ファイルの中身も検索する", indexer.Enabled,
            "指定したフォルダーの文書（テキスト・Office・PDF など）の本文を索引にします。索引はこの PC のあなたの領域に保存され、読む権限のあるファイルだけが入ります。");
        var roots = new StackPanel { Spacing = 4 };
        void RenderRoots()
        {
            roots.Children.Clear();
            foreach (var path in indexer.Roots)
            {
                var row = Row(path, "");
                row.Buttons.Children.Add(SmallButton("", "対象から外す", () => { indexer.RemoveRoot(path); RenderRoots(); }));
                roots.Children.Add(row.Grid);
            }
            if (indexer.Roots.Count == 0) roots.Children.Add(Note("対象のフォルダーがありません。ドキュメントなど、よく探すフォルダーを追加してください。"));
        }
        toggle.Toggled += (_, _) => { indexer.SetEnabled(toggle.IsOn); UpdateStatus(); };
        var add = ActionButton("", "フォルダーを追加…", async () =>
        {
            if (await SuggestMoveDialog.PickFolderAsync() is { } p) { indexer.AddRoot(p); RenderRoots(); UpdateStatus(); }
        });
        var cancel = ActionButton("", "読み込みを中止", () => { indexer.Cancel(); UpdateStatus(); });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(cancel);
        body.Children.Add(toggle);
        body.Children.Add(roots);
        body.Children.Add(actions);
        body.Children.Add(status);
        body.Children.Add(Note("検索欄で content:語 と書くと本文だけを探します。オンのときは、名前の検索結果のあとに「本文に一致」も並びます。"));
        RenderRoots();
        UpdateStatus();
        return Section("ファイルの中身の検索（本文検索）", body);
    }

    // ================================================================ FileBox が変更した Windows の設定（AGENTS: 変更前の値を記録し、オフで戻せる）

    static StackPanel ChangesSection(UserStore store, Action refresh)
    {
        var body = new StackPanel { Spacing = 6 };
        void Render()
        {
            body.Children.Clear();
            body.Children.Add(Note("FileBox が Windows に加えた変更の一覧です。ここから個別に、またはまとめて元に戻せます。"));
            int count = 0;
            var explorer = ExplorerIntegration.Instance;
            if (explorer.IsEnabled || explorer.IsActiveInRegistry())
            {
                count++;
                var row = Row("フォルダーを開くときに FileBox を使う（レジストリ: HKCU\\Software\\Classes\\Directory・Drive）", $"変更前の値を {store.GetRegistryBackups().Count} 件記録しています");
                row.Buttons.Children.Add(SmallButton("", "元に戻す", () => { explorer.Disable(); Render(); refresh(); }));
                body.Children.Add(row.Grid);
            }
            if (explorer.WinEEnabled)
            {
                count++;
                var row = Row("Win + E を FileBox で受ける（起動中だけのキーボードフック）", "FileBox を終了すると元に戻ります");
                row.Buttons.Children.Add(SmallButton("", "オフにする", () => { explorer.SetWinE(false); Render(); refresh(); }));
                body.Children.Add(row.Grid);
            }
            if (GlobalHotkey.Instance.Enabled)
            {
                count++;
                var row = Row($"検索のホットキー {GlobalHotkey.Choices[GlobalHotkey.Instance.ChoiceIndex].Label}（起動中だけ）", "FileBox を終了すると元に戻ります");
                row.Buttons.Children.Add(SmallButton("", "オフにする", () => { GlobalHotkey.Instance.Set(false, GlobalHotkey.Instance.ChoiceIndex); Render(); refresh(); }));
                body.Children.Add(row.Grid);
            }
            if (ConversionContextMenu.Enabled(store))
            {
                count++;
                var row = Row("エクスプローラーの変換・整理メニュー", "現在のユーザーだけに登録しています");
                row.Buttons.Children.Add(SmallButton("", "登録を外す", () => { ConversionContextMenu.Apply(store, false); Render(); }));
                body.Children.Add(row.Grid);
            }
            if (LinkService.IsProtocolRegistered())
            {
                count++;
                var row = Row("filebox:// のリンクを開く登録（レジストリ: HKCU\\Software\\Classes\\filebox）", "外すと、共有されたリンクを FileBox で開けなくなります");
                row.Buttons.Children.Add(SmallButton("", "登録を外す", () => { LinkService.SetProtocolEnabled(store, false); Render(); }));
                body.Children.Add(row.Grid);
            }
            else if (LinkService.ProtocolDisabled(store))
            {
                var row = Row("filebox:// のリンクを開く登録は外しています", "");
                row.Buttons.Children.Add(SmallButton("", "登録し直す", () => { LinkService.SetProtocolEnabled(store, true); Render(); }));
                body.Children.Add(row.Grid);
            }
            foreach (var (drive, share) in NetworkDrives.Mapped(store))
            {
                count++;
                var row = Row($"ネットワークドライブ {drive} → {share}", "FileBox で割り当てました");
                row.Buttons.Children.Add(SmallButton("", "切断する", () =>
                {
                    if (NetworkDrives.Disconnect(store, drive) is int code) App.Window?.ShowInfo(Core.Files.FriendlyError.FromWin32(code).ToString(), InfoBarSeverity.Warning);
                    Render();
                }));
                body.Children.Add(row.Grid);
            }
            if (PolicyService.Instance.IsAdmin)
                body.Children.Add(Note("NTFS のアクセス権（OS 強制モード）とディスククォータは、管理の画面（ユーザーと権限）でユーザーごとに確認・解除できます。"));
            if (count == 0) body.Children.Add(Note("いま FileBox が変更している Windows の設定はありません。"));
            else
            {
                var all = ActionButton("", "すべて元に戻す", () =>
                {
                    try
                    {
                        var explorer2 = ExplorerIntegration.Instance;
                        if (explorer2.IsEnabled || explorer2.IsActiveInRegistry()) explorer2.Disable();
                        if (explorer2.WinEEnabled) explorer2.SetWinE(false);
                        if (GlobalHotkey.Instance.Enabled) GlobalHotkey.Instance.Set(false, GlobalHotkey.Instance.ChoiceIndex);
                        if (ConversionContextMenu.Enabled(store)) ConversionContextMenu.Apply(store, false);
                        foreach (var drive in NetworkDrives.Mapped(store).Keys.ToList()) NetworkDrives.Disconnect(store, drive);
                    }
                    catch (Exception ex) { App.ShowError("元に戻せませんでした", ex); }
                    Render();
                    refresh();
                });
                body.Children.Add(all);
                body.Children.Add(Note("filebox:// の登録は、リンクを使えるように残します（必要なら上の「登録を外す」で外せます）。"));
            }
        }
        Render();
        return body;
    }

    // ================================================================ 部品

    static ToggleSwitch Toggle(string header, bool on, string? description = null)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "オン", OffContent = "オフ" };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            head.Children.Add(new TextBlock
            {
                Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                Foreground = Secondary,
            });
        toggle.Header = head;
        return toggle;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Foreground = Secondary };

    static Expander Section(string header, UIElement body) => new()
    {
        Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    static (Grid Grid, StackPanel Buttons) Row(string title, string detail)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 4, 4, 4), CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return (grid, buttons);
    }

    static Button SmallButton(string glyph, string text, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var b = new Button { Content = content, Padding = new Thickness(8, 3, 8, 3) }.Named(text);
        b.Click += (_, _) => action();
        return b;
    }

    static Button ActionButton(string glyph, string text, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content }.Named(text);
        b.Click += (_, _) => action();
        return b;
    }

    static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}
