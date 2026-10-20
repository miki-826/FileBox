using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace FileBox.App.Services;

/// <summary>
/// 一覧の縮小画像（Windows のサムネイルをそのまま使う。画像・動画・PDF・Office など、Windows が作れる種類すべて）。
/// 最近使った 400 件をメモリに持ち、更新日時が変わったら作り直す。作れない種類は null。
/// </summary>
public static class ThumbnailCache
{
    const int Capacity = 400;
    static readonly LinkedList<(string Key, ImageSource Image)> Order = new();
    static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Map = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim Throttle = new(4);

    static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".jfif", ".ico",
        ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v", ".webm",
        ".pdf", ".docx", ".xlsx", ".pptx", ".psd", ".svg",
    };

    /// <summary>この種類は縮小画像を作れる見込みがあるか（作れない種類で無駄に問い合わせない）。</summary>
    public static bool CanHave(string ext) => Supported.Contains(ext);

    /// <summary>縮小画像を取得する（UI スレッドから呼ぶ）。size は表示する一辺のピクセル数。</summary>
    public static async Task<ImageSource?> GetAsync(string path, uint size)
    {
        string key;
        try { key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{(size > 96 ? 256 : 96)}"; }
        catch (Exception) { return null; }
        if (Map.TryGetValue(key, out var node))
        {
            Order.Remove(node);
            Order.AddFirst(node);
            return node.Value.Image;
        }
        if (Failed.Contains(key)) return null;
        await Throttle.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size > 96 ? 256u : 96u, ThumbnailOptions.ResizeThumbnail);
            // 種類のアイコンしか返らない（本当の縮小画像でない）ときは使わない
            if (thumb == null || thumb.Type != ThumbnailType.Image) { Failed.Add(key); return null; }
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            var added = Order.AddFirst((key, bitmap));
            Map[key] = added;
            while (Order.Count > Capacity)
            {
                Map.Remove(Order.Last!.Value.Key);
                Order.RemoveLast();
            }
            return bitmap;
        }
        catch (Exception)
        {
            Failed.Add(key);
            return null;
        }
        finally { Throttle.Release(); }
    }
}
