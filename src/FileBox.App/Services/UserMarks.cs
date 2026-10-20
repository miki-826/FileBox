using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// タグと星（FileBox の中だけの印。Windows 側のファイルやプロパティは変えない）。一覧の表示用にメモリに持ち、変更は user.db に書く。
/// FileBox で名前変更・移動したものは FileOperations が user.db のパスを付け替えるので、Reload で追従する。
/// </summary>
public static class UserMarks
{
    static UserStore? _store;
    static Dictionary<string, List<string>> _tags = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> _stars = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    /// <summary>印が変わった（一覧・サイドバーを描き直す）。</summary>
    public static event Action? Changed;

    public static void Initialize(UserStore store)
    {
        _store = store;
        Reload();
    }

    public static void Reload()
    {
        if (_store == null) return;
        lock (Gate)
        {
            _tags = _store.GetAllTags();
            _stars = _store.GetStars();
        }
    }

    static string Key(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    public static IReadOnlyList<string> TagsOf(string path)
    {
        lock (Gate) return _tags.TryGetValue(Key(path), out var list) ? list.ToList() : Array.Empty<string>();
    }

    public static bool IsStarred(string path)
    {
        lock (Gate) return _stars.Contains(Key(path));
    }

    public static IReadOnlyList<string> AllTagNames() => _store?.GetTagNames() ?? new();

    public static IReadOnlyList<string> PathsWithTag(string name) => _store?.GetTaggedPaths(name) ?? new();

    public static IReadOnlyList<string> StarredPaths()
    {
        lock (Gate) return _stars.ToList();
    }

    public static void SetTag(IEnumerable<string> paths, string name, bool on)
    {
        if (_store == null || string.IsNullOrWhiteSpace(name)) return;
        foreach (var p in paths)
        {
            if (on) _store.AddTag(Key(p), name.Trim());
            else _store.RemoveTag(Key(p), name.Trim());
        }
        Reload();
        Changed?.Invoke();
    }

    public static void DeleteTag(string name)
    {
        _store?.DeleteTagName(name);
        Reload();
        Changed?.Invoke();
    }

    public static void SetStar(IEnumerable<string> paths, bool on)
    {
        if (_store == null) return;
        foreach (var p in paths) _store.SetStar(Key(p), on);
        Reload();
        Changed?.Invoke();
    }

    /// <summary>パスが変わったあとに呼ぶ（一覧の印を最新にする）。</summary>
    public static void NotifyPathsChanged()
    {
        Reload();
        Changed?.Invoke();
    }
}
