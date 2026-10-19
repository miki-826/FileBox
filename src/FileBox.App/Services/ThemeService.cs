using FileBox.Data;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace FileBox.App.Services;

/// <summary>
/// 見た目のテーマ（Windows に合わせる／ライト／ダーク）とアクセント色。テーマはすぐ反映し、
/// アクセント色はアプリの起動時に読み込む（ボタンや選択の色に使われ、作り直しが要るため次の起動から）。
/// </summary>
public static class ThemeService
{
    const string ThemeKey = "view.theme";
    const string AccentKey = "view.accent";

    public static readonly (string Label, ElementTheme Theme)[] Themes =
    {
        ("Windows の設定に合わせる", ElementTheme.Default),
        ("ライト", ElementTheme.Light),
        ("ダーク", ElementTheme.Dark),
    };

    public static ElementTheme Theme(UserStore store) => Enum.TryParse<ElementTheme>(store.GetSetting(ThemeKey), out var t) ? t : ElementTheme.Default;

    /// <summary>アクセント色（#RRGGBB）。null は Windows の色。</summary>
    public static string? Accent(UserStore store) => store.GetSetting(AccentKey) is { Length: 7 } hex ? hex : null;

    public static void SetTheme(UserStore store, ElementTheme theme)
    {
        store.SetSetting(ThemeKey, theme.ToString());
        App.Window?.ApplyTheme(theme);
    }

    public static void SetAccent(UserStore store, string? hex) => store.SetSetting(AccentKey, hex ?? "");

    /// <summary>アプリの起動直後（ウィンドウを作る前）に、アクセント色の資源を差し替える。</summary>
    public static void ApplyAccent(Application app, string? hex)
    {
        if (hex == null || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)) return;
        var c = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        app.Resources["SystemAccentColor"] = c;
        app.Resources["SystemAccentColorLight1"] = Mix(c, Colors.White, 0.2);
        app.Resources["SystemAccentColorLight2"] = Mix(c, Colors.White, 0.4);
        app.Resources["SystemAccentColorLight3"] = Mix(c, Colors.White, 0.6);
        app.Resources["SystemAccentColorDark1"] = Mix(c, Colors.Black, 0.2);
        app.Resources["SystemAccentColorDark2"] = Mix(c, Colors.Black, 0.4);
        app.Resources["SystemAccentColorDark3"] = Mix(c, Colors.Black, 0.6);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
