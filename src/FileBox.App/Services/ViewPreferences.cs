using System.Text.Json;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// 場所ごとの表示の好み（フォルダの色・日付でグループ化）。user.db の settings に JSON で持つ。
/// 色は FileBox の中だけの表示で、Windows 側（desktop.ini など）は変えない。
/// </summary>
public static class ViewPreferences
{
    /// <summary>フォルダに付けられる色（Teams のチャネル色に近い、明暗どちらのテーマでも読める中間の色）。</summary>
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("赤", "#E5484D"), ("オレンジ", "#F76B15"), ("黄", "#E2A300"), ("緑", "#30A46C"), ("水色", "#05A2C2"),
        ("青", "#3E63DD"), ("紫", "#8E4EC6"), ("ピンク", "#D6409F"), ("茶", "#A07553"), ("グレー", "#8B8D98"),
    };

    const string ColorsKey = "view.folderColors";
    const string GroupsKey = "view.dateGroups";

    static UserStore? _store;
    static Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, bool> _groups = new(StringComparer.OrdinalIgnoreCase);
    static string? _downloads;

    /// <summary>フォルダの色が変わった（一覧・サイドバーを描き直す）。</summary>
    public static event Action? ColorsChanged;

    public static void Initialize(UserStore store)
    {
        _store = store;
        _colors = Load<string>(ColorsKey);
        _groups = Load<bool>(GroupsKey);
        _downloads = ShellService.KnownFolder(ShellService.QuickAccess[2].Id);
    }

    static Dictionary<string, T> Load<T>(string key)
    {
        try
        {
            if (_store?.GetSetting(key) is { } json && JsonSerializer.Deserialize<Dictionary<string, T>>(json) is { } map)
                return new(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static string Key(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    // ---------------------------------------------------------------- フォルダの色

    /// <summary>フォルダに付けた色（#RRGGBB）。付けていなければ null。一覧の読み込み（別スレッド）からも呼ばれる。</summary>
    public static string? FolderColor(string path)
    {
        lock (_colors) return _colors.TryGetValue(Key(path), out var hex) ? hex : null;
    }

    public static void SetFolderColor(string path, string? hex)
    {
        lock (_colors)
        {
            if (hex == null) _colors.Remove(Key(path));
            else _colors[Key(path)] = hex;
            _store?.SetSetting(ColorsKey, JsonSerializer.Serialize(_colors));
        }
        ColorsChanged?.Invoke();
    }

    // ---------------------------------------------------------------- 日付でグループ化

    /// <summary>この場所を「今日・昨日・今週・先週…」でまとめて表示するか。ダウンロードは既定でまとめる（エクスプローラーと同じ）。</summary>
    public static bool GroupByDate(string path)
    {
        if (path.Length == 0) return false;
        if (_groups.TryGetValue(Key(path), out bool on)) return on;
        return _downloads != null && string.Equals(Key(path), Key(_downloads), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetGroupByDate(string path, bool on)
    {
        _groups[Key(path)] = on;
        _store?.SetSetting(GroupsKey, JsonSerializer.Serialize(_groups));
    }
}
