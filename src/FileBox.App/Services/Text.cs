using System.Text.Json;
using FileBox.Core.Settings;
using FileBox.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileBox.App.Services;

/// <summary>
/// 画面の言語（日本語・English）。文字列は日本語のまま書き、英語のときだけ Assets\Strings\en.json の対応表で置き換える
/// （照合は Core の Translator）。画面に出ている文字（ボタン・メニュー・ダイアログ・通知など）は、英語のときに定期的に
/// 見回って置き換える。ファイル名などのデータ（Tag="data" の TextBlock）は置き換えない。切り替えは次の起動から。
/// </summary>
public static class Text
{
    const string LanguageKey = "ui.language";

    static Translator? _translator;

    public static bool IsEnglish => _translator != null;

    public static readonly (string Code, string Label)[] Languages = { ("ja", "日本語"), ("en", "English") };

    public static string Language(UserStore store) => store.GetSetting(LanguageKey) == "en" ? "en" : "ja";

    public static void SetLanguage(UserStore store, string code) => store.SetSetting(LanguageKey, code);

    public static void Initialize(UserStore store)
    {
        if (Language(store) != "en") return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Strings", "en.json");
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            _translator = new Translator(map);
        }
        catch (Exception ex) { AppLog.Error("英語の文字列を読み込めませんでした。日本語で表示します", ex); }
    }

    /// <summary>英語のときは対応する英語に、それ以外はそのまま返す。対応がなければ元の文のまま。</summary>
    public static string T(string ja)
    {
        if (_translator == null || string.IsNullOrEmpty(ja) || !Translator.HasJapanese(ja)) return ja;
        return _translator.Translate(ja) ?? ja;
    }

    // ================================================================ 画面の置き換え

    static Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    /// <summary>英語のとき、画面と開いているポップアップ（メニュー・ダイアログ・ツールチップ）を見回って置き換える。</summary>
    public static void Start(Window window)
    {
        if (!IsEnglish) return;
        window.Title = T(window.Title ?? "");
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(350);
        _timer.Tick += (_, _) =>
        {
            if (window.Content is not FrameworkElement root || root.XamlRoot == null) return;
            Apply(root);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
                if (popup.Child != null) Apply(popup.Child);
        };
        _timer.Start();
    }

    public static void Apply(DependencyObject element)
    {
        if (!IsEnglish) return;
        Visit(element);
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++) Apply(VisualTreeHelper.GetChild(element, i));
    }

    static void Visit(DependencyObject e)
    {
        switch (e)
        {
            case TextBlock tb when !Equals(tb.Tag, "data") && tb.Inlines.Count <= 1:
                Set(tb.Text, v => tb.Text = v);
                break;
            case TextBox box:
                Set(box.PlaceholderText, v => box.PlaceholderText = v);
                if (box.Header is string th) Set(th, v => box.Header = v);
                break;
            case PasswordBox pb:
                if (pb.Header is string ph) Set(ph, v => pb.Header = v);
                break;
            case AutoSuggestBox asb:
                Set(asb.PlaceholderText, v => asb.PlaceholderText = v);
                break;
            case NumberBox nb:
                if (nb.Header is string nh) Set(nh, v => nb.Header = v);
                break;
            case ComboBox cb:
                if (cb.Header is string ch) Set(ch, v => cb.Header = v);
                Set(cb.PlaceholderText, v => cb.PlaceholderText = v);
                break;
            case ToggleSwitch ts:
                if (ts.OnContent is string on) Set(on, v => ts.OnContent = v);
                if (ts.OffContent is string off) Set(off, v => ts.OffContent = v);
                if (ts.Header is string tsh) Set(tsh, v => ts.Header = v);
                break;
            case MenuFlyoutItem mi:
                Set(mi.Text, v => mi.Text = v);
                Set(mi.KeyboardAcceleratorTextOverride, v => mi.KeyboardAcceleratorTextOverride = v);
                break;
            case MenuFlyoutSubItem ms:
                Set(ms.Text, v => ms.Text = v);
                break;
            case AppBarButton ab:
                Set(ab.Label, v => ab.Label = v);
                break;
            case AppBarToggleButton atb:
                Set(atb.Label, v => atb.Label = v);
                break;
            case InfoBar info:
                Set(info.Title, v => info.Title = v);
                Set(info.Message, v => info.Message = v);
                break;
            case ContentDialog dialog:
                if (dialog.Title is string dt) Set(dt, v => dialog.Title = v);
                Set(dialog.PrimaryButtonText, v => dialog.PrimaryButtonText = v);
                Set(dialog.SecondaryButtonText, v => dialog.SecondaryButtonText = v);
                Set(dialog.CloseButtonText, v => dialog.CloseButtonText = v);
                break;
            case TeachingTip tip:
                Set(tip.Title, v => tip.Title = v);
                Set(tip.Subtitle, v => tip.Subtitle = v);
                if (tip.ActionButtonContent is string tac) Set(tac, v => tip.ActionButtonContent = v);
                if (tip.CloseButtonContent is string tcc) Set(tcc, v => tip.CloseButtonContent = v);
                break;
            case Expander ex:
                if (ex.Header is string eh) Set(eh, v => ex.Header = v);
                break;
            case TabViewItem tab:
                if (tab.Header is string tabh) Set(tabh, v => tab.Header = v);
                break;
            case ContentControl cc when cc.Content is string s:
                Set(s, v => cc.Content = v);
                break;
        }
        if (e is UIElement ui)
        {
            if (ToolTipService.GetToolTip(ui) is string tip) Set(tip, v => ToolTipService.SetToolTip(ui, v));
            Set(AutomationProperties.GetName(ui), v => AutomationProperties.SetName(ui, v));
        }
    }

    static void Set(string? value, Action<string> apply)
    {
        if (_translator == null || string.IsNullOrEmpty(value) || !Translator.HasJapanese(value)) return;
        if (_translator.Translate(value) is { } en && en != value) apply(en);
    }
}
