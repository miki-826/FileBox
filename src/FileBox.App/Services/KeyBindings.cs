using FileBox.Data;
using Windows.System;

namespace FileBox.App.Services;

/// <summary>キーの割り当て 1 つ。Id は操作の名前、Label は一覧での説明。</summary>
public sealed record KeyBinding(string Id, string Label, string Group, VirtualKey Key, VirtualKeyModifiers Modifiers)
{
    public string KeyText => Format(Key, Modifiers);

    public static string Format(VirtualKey key, VirtualKeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        parts.Add(key switch
        {
            VirtualKey.Left => "←",
            VirtualKey.Right => "→",
            VirtualKey.Up => "↑",
            VirtualKey.Down => "↓",
            VirtualKey.Tab => "Tab",
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)key - (int)VirtualKey.Number0).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}

/// <summary>
/// ウィンドウ全体のショートカット（F1 の一覧と、割り当ての変更）。変更は user.db の settings（keys.操作名）に持ち、次の起動から反映する。
/// 一覧の中だけで効くキー（Enter・Delete・F2・Ctrl+C など）は固定で、一覧には「変えられないキー」として載せる。
/// </summary>
public static class KeyBindings
{
    public static readonly KeyBinding[] Defaults =
    {
        new("newTab", "新しいタブ", "タブと区画", VirtualKey.T, VirtualKeyModifiers.Control),
        new("closePane", "区画（最後ならタブ）を閉じる", "タブと区画", VirtualKey.W, VirtualKeyModifiers.Control),
        new("reopenTab", "閉じたタブを開き直す", "タブと区画", VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        new("nextTab", "次のタブ", "タブと区画", VirtualKey.Tab, VirtualKeyModifiers.Control),
        new("prevTab", "前のタブ", "タブと区画", VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        new("search", "検索欄へ", "移動と検索", VirtualKey.F, VirtualKeyModifiers.Control),
        new("search2", "検索欄へ（エクスプローラーと同じ）", "移動と検索", VirtualKey.E, VirtualKeyModifiers.Control),
        new("address", "アドレス欄を編集", "移動と検索", VirtualKey.L, VirtualKeyModifiers.Control),
        new("address2", "アドレス欄を編集（エクスプローラーと同じ）", "移動と検索", VirtualKey.D, VirtualKeyModifiers.Menu),
        new("back", "戻る", "移動と検索", VirtualKey.Left, VirtualKeyModifiers.Menu),
        new("forward", "進む", "移動と検索", VirtualKey.Right, VirtualKeyModifiers.Menu),
        new("up", "上へ", "移動と検索", VirtualKey.Up, VirtualKeyModifiers.Menu),
        new("refresh", "最新の情報に更新", "移動と検索", VirtualKey.F5, VirtualKeyModifiers.None),
        new("undo", "元に戻す", "ファイル操作", VirtualKey.Z, VirtualKeyModifiers.Control),
        new("redo", "やり直す", "ファイル操作", VirtualKey.Y, VirtualKeyModifiers.Control),
        new("hidden", "隠しファイルの表示を切り替え", "表示", VirtualKey.H, VirtualKeyModifiers.Control),
        new("zoomIn", "大きく", "表示", VirtualKey.Add, VirtualKeyModifiers.Control),
        new("zoomOut", "小さく", "表示", VirtualKey.Subtract, VirtualKeyModifiers.Control),
        new("help", "ショートカット一覧", "表示", VirtualKey.F1, VirtualKeyModifiers.None),
    };

    /// <summary>一覧の中だけで効く、変えられないキー（説明用）。</summary>
    public static readonly (string Keys, string Label)[] Fixed =
    {
        ("Enter", "開く"), ("Alt+Enter", "プロパティ"), ("Space", "プレビュー・詳細"), ("F2", "名前の変更（複数なら一括）"),
        ("Delete", "ごみ箱へ"), ("Ctrl+C / Ctrl+X / Ctrl+V", "コピー・切り取り・貼り付け"), ("Ctrl+Shift+C", "パスをコピー"),
        ("Ctrl+Shift+N", "新しいフォルダー"), ("Ctrl+A", "すべて選択"), ("Backspace", "戻る"), ("文字キー", "その文字で始まる項目へ移動"),
        ("Ctrl+1〜9", "その番号のタブへ"), ("Ctrl+\\ / Ctrl+Shift+\\", "左右・上下に分割"), ("Ctrl+ホイール", "大きさを変える"),
    };

    static UserStore? _store;

    public static void Initialize(UserStore store) => _store = store;

    /// <summary>今の割り当て（変更があればそれ）。</summary>
    public static IReadOnlyList<KeyBinding> Current => Defaults.Select(d => Load(d) ?? d).ToList();

    static KeyBinding? Load(KeyBinding d)
    {
        if (_store?.GetSetting("keys." + d.Id) is not { } value) return null;
        var parts = value.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<VirtualKey>(parts[0], out var key) || !Enum.TryParse<VirtualKeyModifiers>(parts[1], out var mods)) return null;
        return d with { Key = key, Modifiers = mods };
    }

    /// <summary>割り当てを変える。ほかの操作と重なるときは、その操作の名前を返して変えない。</summary>
    public static string? Set(string id, VirtualKey key, VirtualKeyModifiers mods)
    {
        var clash = Current.FirstOrDefault(b => b.Id != id && b.Key == key && b.Modifiers == mods);
        if (clash != null) return clash.Label;
        _store?.SetSetting("keys." + id, $"{key}|{mods}");
        return null;
    }

    public static void Reset(string id) => _store?.SetSetting("keys." + id, "");
}
