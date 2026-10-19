using System.Globalization;
using FileBox.Core.Security;
using Windows.Storage;

namespace FileBox.App.Services;

/// <summary>
/// Windows のプロパティ（撮影日時・長さ・作成者など）の読み書き。プレビューの詳細と、一覧の追加の列で共用する。
/// 書き込みは Windows のプロパティ ハンドラーがある種類（写真・音楽・Office など）だけで、権限を確かめて監査に残す。
/// </summary>
public static class FileProperties
{
    /// <summary>詳細に表示するもの（ある項目だけ出す）。</summary>
    public static readonly (string Key, string Label)[] Details =
    {
        ("System.Title", "タイトル"),
        ("System.Author", "作成者"),
        ("System.Document.LastAuthor", "最終更新者"),
        ("System.Company", "会社"),
        ("System.Image.Dimensions", "大きさ"),
        ("System.Photo.DateTaken", "撮影日時"),
        ("System.Photo.CameraModel", "カメラ"),
        ("System.Media.Duration", "長さ"),
        ("System.Video.FrameWidth", "フレーム幅"),
        ("System.Video.FrameHeight", "フレーム高"),
        ("System.Music.Artist", "アーティスト"),
        ("System.Music.AlbumTitle", "アルバム"),
        ("System.Keywords", "タグ"),
        ("System.Comment", "コメント"),
    };

    /// <summary>一覧に足せる列。Key が "created" のものは Windows のプロパティでなくファイルの作成日時。</summary>
    public static readonly (string Key, string Label)[] Columns =
    {
        ("created", "作成日時"),
        ("System.Photo.DateTaken", "撮影日時"),
        ("System.Image.Dimensions", "大きさ"),
        ("System.Media.Duration", "長さ"),
        ("System.Author", "作成者"),
        ("System.Title", "タイトル"),
    };

    /// <summary>詳細の画面で編集できるもの（値の型）。</summary>
    public static readonly Dictionary<string, Type> Editable = new()
    {
        ["System.Title"] = typeof(string),
        ["System.Author"] = typeof(string[]),
        ["System.Comment"] = typeof(string),
        ["System.Keywords"] = typeof(string[]),
        ["System.Company"] = typeof(string),
        ["System.Music.Artist"] = typeof(string[]),
        ["System.Music.AlbumTitle"] = typeof(string),
        ["System.Photo.DateTaken"] = typeof(DateTimeOffset),
    };

    public static async Task<Dictionary<string, string>> ReadAsync(string path, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, string>();
        var wanted = keys.ToList();
        if (wanted.Remove("created"))
        {
            try { result["created"] = File.GetCreationTime(path).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture); }
            catch (Exception) { }
        }
        if (wanted.Count == 0 || Directory.Exists(path)) return result;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.RetrievePropertiesAsync(wanted);
            foreach (var key in wanted)
                if (props.TryGetValue(key, out var value) && Format(key, value) is { Length: > 0 } text) result[key] = text;
        }
        catch (Exception) { }   // 対応していない種類・アクセスできないファイル
        return result;
    }

    public static string? Format(string key, object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        string[] arr => string.Join("; ", arr.Where(v => !string.IsNullOrWhiteSpace(v))),
        DateTimeOffset d => d.LocalDateTime.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture),
        ulong ticks when key == "System.Media.Duration" => TimeSpan.FromTicks((long)ticks).ToString(ticks >= (ulong)TimeSpan.TicksPerHour ? @"h\:mm\:ss" : @"m\:ss"),
        uint n when key.StartsWith("System.Video.Frame", StringComparison.Ordinal) => $"{n} px",
        _ => value.ToString(),
    };

    /// <summary>プロパティを書き換える。text は表示と同じ形式（複数の値は「;」区切り、日時は yyyy/MM/dd HH:mm）。</summary>
    public static async Task<bool> WriteAsync(string path, string key, string text)
    {
        if (!Editable.TryGetValue(key, out var type) || !Guard.Can(Permission.Modify, path, "edit")) return false;
        object? value;
        if (type == typeof(string[])) value = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (type == typeof(DateTimeOffset))
        {
            if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d))
            {
                App.Window?.ShowInfo("日時は 2026/10/20 14:30 の形で入力してください", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
                return false;
            }
            value = new DateTimeOffset(d);
        }
        else value = text;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await file.Properties.SavePropertiesAsync(new[] { new KeyValuePair<string, object?>(key, value) });
            Guard.Done("edit", path, detail: $"プロパティ {key}");
            return true;
        }
        catch (Exception ex)
        {
            App.ShowError("プロパティを変更できませんでした（この種類のファイルは書き換えられない可能性があります）", ex, path);
            return false;
        }
    }
}
