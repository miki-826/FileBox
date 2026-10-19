using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBox.Core.Settings;

public sealed record PackageFavorite(string Path, string? Label);
public sealed record PackageSearch(string Name, string Query);
public sealed record PackageWorkspace(string Name, string Layout);
public sealed record PackageRule(string? NamePattern, string? Ext, string TargetPath, bool AutoMove);
public sealed record PackageTag(string Path, string Name);

/// <summary>
/// 設定の書き出し・取り込み（PC の乗り換え用）。表示の好み・お気に入り・保存した検索・作業・振り分けルール・タグを持ち運ぶ。
/// Windows 側を変える設定（エクスプローラーの置き換え・Win+E・ホットキー）と、秘密の値（リンクの鍵）は含めない。
/// 取り込んだ先で改めて利用者がオンにする。
/// </summary>
public sealed record SettingsPackage
{
    public const string FormatName = "FileBox.Settings";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public DateTime ExportedAt { get; init; }
    public Dictionary<string, string> Settings { get; init; } = new();
    public List<PackageFavorite> Favorites { get; init; } = new();
    public List<PackageSearch> SavedSearches { get; init; } = new();
    public List<PackageWorkspace> Workspaces { get; init; } = new();
    public List<PackageRule> InboxRules { get; init; } = new();
    public List<PackageTag> Tags { get; init; } = new();
    public List<string> Stars { get; init; } = new();
    public List<string> SuggestExcluded { get; init; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>持ち運ばない設定（Windows 側を変えるもの・秘密・この PC だけの状態）。</summary>
    public static bool IsPortable(string key) =>
        !(key.StartsWith("explorer.", StringComparison.Ordinal) || key.StartsWith("hotkey.", StringComparison.Ordinal) ||
          key is "links.httpSecret" or "links.httpPort" or "inbox.seeded" or "onboarding.done" or "update.lastChecked");

    public string ToJson() => JsonSerializer.Serialize(this with { Settings = Settings.Where(kv => IsPortable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) }, Options);

    /// <summary>読み込んで形式を確かめる。FileBox の設定ファイルでない・新しすぎる版なら理由を error に入れて null。</summary>
    public static SettingsPackage? Parse(string json, out string? error)
    {
        error = null;
        SettingsPackage? p;
        try { p = JsonSerializer.Deserialize<SettingsPackage>(json, Options); }
        catch (JsonException ex) { error = $"設定ファイルを読み込めませんでした: {ex.Message}"; return null; }
        if (p == null || p.Format != FormatName) { error = "FileBox の設定ファイルではありません"; return null; }
        if (p.Version > CurrentVersion) { error = "新しい版の FileBox で書き出された設定です。FileBox を更新してから取り込んでください"; return null; }
        return p with
        {
            Settings = (p.Settings ?? new()).Where(kv => IsPortable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Favorites = (p.Favorites ?? new()).Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList(),
            SavedSearches = (p.SavedSearches ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList(),
            Workspaces = (p.Workspaces ?? new()).Where(w => !string.IsNullOrWhiteSpace(w.Name) && w.Layout.Length > 0).ToList(),
            InboxRules = (p.InboxRules ?? new()).Where(r => !string.IsNullOrWhiteSpace(r.TargetPath)).ToList(),
            Tags = (p.Tags ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.Path) && !string.IsNullOrWhiteSpace(t.Name)).ToList(),
            Stars = p.Stars ?? new(),
            SuggestExcluded = p.SuggestExcluded ?? new(),
        };
    }
}
