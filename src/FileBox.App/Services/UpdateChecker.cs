using System.Net.Http;
using System.Text.Json;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// 新しい preview 版が出ていないかの確認（1 日 1 回まで。設定でオフにできる）。GitHub の公開ページの情報を読むだけで、
/// こちらの情報は送らない。ダウンロードと導入は自動では行わず、配布ページを開く。
/// </summary>
public static class UpdateChecker
{
    public const string ReleasePage = "https://github.com/miki-826/FileBox/releases/tag/preview";
    const string ApiUrl = "https://api.github.com/repos/miki-826/FileBox/releases/tags/preview";
    const string AssetName = "FileBox-Installer-preview-win-x64.exe";

    public static bool Enabled(UserStore store) => store.GetSetting("update.check") != "0";

    public static void SetEnabled(UserStore store, bool on) => store.SetSetting("update.check", on ? "1" : "0");

    /// <summary>新しい版があれば公開日時を返す。確認しなかった・無かった・失敗したときは null。force で 1 日 1 回の制限を無視する。</summary>
    public static async Task<DateTime?> CheckAsync(UserStore store, bool force = false)
    {
        if (!force && !Enabled(store)) return null;
        if (!force && DateTime.TryParse(store.GetSetting("update.lastChecked"), out var last) && DateTime.Now - last < TimeSpan.FromDays(1)) return null;
        store.SetSetting("update.lastChecked", DateTime.Now.ToString("o"));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FileBox-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ApiUrl));
            DateTime? published = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;
                published = asset.GetProperty("updated_at").GetDateTime().ToLocalTime();
            }
            if (published is not DateTime p) return null;
            // 実行中の本体を作った日時より 1 時間以上新しければ、新しい版とみなす
            var built = File.GetLastWriteTime(Environment.ProcessPath ?? typeof(UpdateChecker).Assembly.Location);
            return p > built.AddHours(1) ? p : null;
        }
        catch (Exception ex)
        {
            AppLog.Info($"更新の確認に失敗しました: {ex.Message}");
            return null;
        }
    }
}
