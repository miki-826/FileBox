using System.Text.Json;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>一覧の表示のしかた。詳細（列あり）・大アイコン・並べて表示（タイル）。</summary>
public enum ViewMode { Details, Icons, Tiles }

/// <summary>
/// 場所ごとの表示の好み（フォルダの色・日付でグループ化・表示のしかた）と、全体の好み（大きさ・追加の列・チェックボックス）。
/// user.db の settings に JSON で持つ。色は FileBox の中だけの表示で、Windows 側（desktop.ini など）は変えない。
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
    const string ModesKey = "view.modes";
    const string ZoomKey = "view.zoom";
    const string ColumnsKey = "view.columns";
    const string CheckBoxKey = "view.checkboxes";

    /// <summary>大きさの段階（Ctrl+ホイール）。1 が既定。</summary>
    public const int MaxZoom = 3;

    static UserStore? _store;
    static Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, bool> _groups = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> _modes = new(StringComparer.OrdinalIgnoreCase);
    static string? _downloads;
    static string? _pictures;

    /// <summary>フォルダの色が変わった（一覧・サイドバーを描き直す）。</summary>
    public static event Action? ColorsChanged;

    /// <summary>大きさ・列・チェックボックスが変わった（開いている一覧すべてに反映する）。</summary>
    public static event Action? LayoutChanged;

    public static void Initialize(UserStore store)
    {
        _store = store;
        _colors = Load<string>(ColorsKey);
        _groups = Load<bool>(GroupsKey);
        _modes = Load<string>(ModesKey);
        _downloads = ShellService.KnownFolder(ShellService.QuickAccess[2].Id);
        _pictures = ShellService.KnownFolder(ShellService.QuickAccess[4].Id);
        Zoom = int.TryParse(store.GetSetting(ZoomKey), out int z) ? Math.Clamp(z, 0, MaxZoom) : 1;
        Columns = (store.GetSetting(ColumnsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        CheckBoxes = store.GetSetting(CheckBoxKey) == "1";
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

    // ---------------------------------------------------------------- 表示のしかた（場所ごと）

    /// <summary>この場所の表示のしかた。ピクチャ（とその中）は既定で大アイコン、それ以外は詳細。</summary>
    public static ViewMode Mode(string path)
    {
        if (path.Length > 0 && _modes.TryGetValue(Key(path), out var s) && Enum.TryParse<ViewMode>(s, out var mode)) return mode;
        if (_pictures != null && path.Length > 0 && (Key(path) + "\\").StartsWith(Key(_pictures) + "\\", StringComparison.OrdinalIgnoreCase)) return ViewMode.Icons;
        return ViewMode.Details;
    }

    public static void SetMode(string path, ViewMode mode)
    {
        if (path.Length == 0) return;
        _modes[Key(path)] = mode.ToString();
        _store?.SetSetting(ModesKey, JsonSerializer.Serialize(_modes));
    }

    // ---------------------------------------------------------------- 全体の好み

    /// <summary>アイコンと文字の大きさ（0〜3）。</summary>
    public static int Zoom { get; private set; } = 1;

    public static void SetZoom(int zoom)
    {
        zoom = Math.Clamp(zoom, 0, MaxZoom);
        if (zoom == Zoom) return;
        Zoom = zoom;
        _store?.SetSetting(ZoomKey, zoom.ToString());
        LayoutChanged?.Invoke();
    }

    /// <summary>詳細表示に足した列（FileProperties.Columns のキー。3 つまで）。</summary>
    public static List<string> Columns { get; private set; } = new();

    public static void ToggleColumn(string key)
    {
        if (!Columns.Remove(key))
        {
            if (Columns.Count >= 3) Columns.RemoveAt(0);
            Columns.Add(key);
        }
        _store?.SetSetting(ColumnsKey, string.Join(',', Columns));
        LayoutChanged?.Invoke();
    }

    /// <summary>項目の左にチェックボックスを出して、クリックで複数選べるようにする（タッチ・マウスだけの操作向け）。</summary>
    public static bool CheckBoxes { get; private set; }

    public static void SetCheckBoxes(bool on)
    {
        CheckBoxes = on;
        _store?.SetSetting(CheckBoxKey, on ? "1" : "0");
        LayoutChanged?.Invoke();
    }
}
