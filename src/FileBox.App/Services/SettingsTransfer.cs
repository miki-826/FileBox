using FileBox.Core.Settings;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// 設定の書き出し・取り込み（PC の乗り換え用）。形式と、持ち運ばない設定の判定は Core の SettingsPackage。
/// 取り込みは足し合わせ（今ある設定・お気に入りは消さない。同じ名前の設定は上書き）。
/// </summary>
public static class SettingsTransfer
{
    public static SettingsPackage Build(UserStore store) => new()
    {
        ExportedAt = DateTime.Now,
        Settings = store.GetAllSettings().ToDictionary(s => s.Key, s => s.Value),
        Favorites = store.GetFavorites().Select(f => new PackageFavorite(f.Path, f.Label)).ToList(),
        SavedSearches = store.GetSavedSearches().Select(s => new PackageSearch(s.Name, s.Query)).ToList(),
        Workspaces = store.GetWorkspaces().Select(w => new PackageWorkspace(w.Name, w.LayoutJson)).ToList(),
        InboxRules = store.GetInboxRules().Select(r => new PackageRule(r.NamePattern, r.Ext, r.TargetPath, r.AutoMove)).ToList(),
        Tags = store.GetAllTags().SelectMany(kv => kv.Value.Select(t => new PackageTag(kv.Key, t))).ToList(),
        Stars = store.GetStars().ToList(),
        SuggestExcluded = store.GetSuggestExcluded(),
    };

    public static async Task<string?> ExportAsync(UserStore store)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"FileBox-設定-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("FileBox の設定", new List<string> { ".json" });
        Initialize(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return null;
        await File.WriteAllTextAsync(file.Path, Build(store).ToJson());
        return file.Path;
    }

    /// <summary>取り込んだ項目の数（キャンセル・失敗は null。失敗の理由は error）。</summary>
    public static async Task<(int Count, string? Error)?> ImportAsync(UserStore store)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        Initialize(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return null;
        var package = SettingsPackage.Parse(await File.ReadAllTextAsync(file.Path), out var error);
        if (package == null) return (0, error);
        int n = 0;
        foreach (var (key, value) in package.Settings) { store.SetSetting(key, value); n++; }
        var favorites = store.GetFavorites().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in package.Favorites.Where(f => !favorites.Contains(f.Path))) { store.AddFavorite(f.Path, f.Label); n++; }
        var searches = store.GetSavedSearches().Select(s => s.Name).ToHashSet();
        foreach (var s in package.SavedSearches.Where(s => !searches.Contains(s.Name))) { store.SaveSearch(s.Name, s.Query); n++; }
        foreach (var w in package.Workspaces) { store.SaveWorkspace(w.Name, w.Layout); n++; }
        var rules = store.GetInboxRules().Select(r => (r.NamePattern, r.Ext, r.TargetPath)).ToHashSet();
        foreach (var r in package.InboxRules.Where(r => !rules.Contains((r.NamePattern, r.Ext, r.TargetPath)))) { store.AddInboxRule(r.NamePattern, r.Ext, r.TargetPath, r.AutoMove); n++; }
        foreach (var t in package.Tags) { store.AddTag(t.Path, t.Name); n++; }
        foreach (var s in package.Stars) { store.SetStar(s, true); n++; }
        foreach (var x in package.SuggestExcluded) { store.SetSuggestExcluded(x, true); n++; }
        UserMarks.NotifyPathsChanged();
        return (n, null);
    }

    static void Initialize(object picker)
    {
        if (App.Window != null) WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
    }
}
