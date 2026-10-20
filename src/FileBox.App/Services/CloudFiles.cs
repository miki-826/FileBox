using FileBox.Core.Security;

namespace FileBox.App.Services;

public enum CloudState { None, OnlineOnly, Local, Pinned }

/// <summary>
/// OneDrive など（Windows のクラウド ファイル）の状態と、「常にこのデバイスに保持」「空き領域を増やす」。
/// 状態はファイルの属性で分かる。切り替えもエクスプローラー（attrib +P / +U）と同じく属性で行い、同期アプリが反映する。
/// </summary>
public static class CloudFiles
{
    const FileAttributes Pinned = (FileAttributes)0x00080000;
    const FileAttributes Unpinned = (FileAttributes)0x00100000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    static readonly string[] Roots = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
        .Select(Environment.GetEnvironmentVariable)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => p!.TrimEnd('\\') + "\\")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>このパスがクラウドと同期する場所の中か。</summary>
    public static bool IsInCloud(string path) => Roots.Any(r => (path.TrimEnd('\\') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase));

    public static CloudState StateOf(string path, FileAttributes attributes)
    {
        bool cloudy = attributes.HasFlag(RecallOnDataAccess) || attributes.HasFlag(RecallOnOpen) || attributes.HasFlag(FileAttributes.Offline);
        if (!cloudy && !attributes.HasFlag(Pinned) && !attributes.HasFlag(Unpinned) && !IsInCloud(path)) return CloudState.None;
        if (attributes.HasFlag(Pinned)) return CloudState.Pinned;
        if (cloudy || attributes.HasFlag(Unpinned)) return CloudState.OnlineOnly;
        return CloudState.Local;
    }

    /// <summary>一覧の印（Segoe Fluent Icons の文字・色・説明）。</summary>
    public static (string Glyph, string Color, string Tip)? Badge(CloudState state) => state switch
    {
        CloudState.OnlineOnly => ("", "#3E8EDE", "オンラインのみ（開くとダウンロードされます）"),
        CloudState.Local => ("", "#30A46C", "このデバイスで使用可能"),
        CloudState.Pinned => ("", "#30A46C", "常にこのデバイスに保持"),
        _ => null,
    };

    /// <summary>「常にこのデバイスに保持」（pin = true）／「空き領域を増やす」（pin = false）。フォルダーは配下にも当てる。</summary>
    public static async Task<int> SetPinnedAsync(IReadOnlyList<string> paths, bool pin)
    {
        if (paths.Any(p => !Guard.Can(Permission.Modify, p, "edit"))) return 0;
        int changed = await Task.Run(() =>
        {
            int n = 0;
            foreach (var path in paths)
            {
                foreach (var target in Expand(path))
                {
                    try
                    {
                        var a = File.GetAttributes(target);
                        var next = pin ? (a | Pinned) & ~Unpinned : (a | Unpinned) & ~Pinned;
                        if (next != a) { File.SetAttributes(target, next); n++; }
                    }
                    catch (Exception ex) { AppLog.Error($"クラウドの保持設定を変更できませんでした: {target}", ex); }
                }
            }
            return n;
        });
        foreach (var p in paths) Guard.Done("edit", p, detail: pin ? "常にこのデバイスに保持" : "空き領域を増やす");
        return changed;
    }

    static IEnumerable<string> Expand(string path)
    {
        yield return path;
        if (!Directory.Exists(path)) yield break;
        // クラウドのプレースホルダーは再解析ポイントなので、飛ばさずに含める
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        int count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
        {
            if (++count > 20000) yield break;   // 大きすぎるフォルダーは上から 2 万件まで
            yield return entry;
        }
    }
}
