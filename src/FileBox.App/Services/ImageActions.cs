using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileBox.App.Services;

/// <summary>
/// 画像の右クリック操作（エクスプローラーと同じ）: その場で 90 度回転する・デスクトップの背景に設定する。
/// </summary>
public static class ImageActions
{
    // Windows の標準コーデックで読み書きできる種類（GIF はアニメーションが 1 枚目だけになるので回転しない）
    static readonly HashSet<string> Rotatable = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".tif", ".tiff" };
    static readonly HashSet<string> Wallpapers = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    public static bool CanRotate(string ext) => Rotatable.Contains(ext);
    public static bool CanBeWallpaper(string ext) => Wallpapers.Contains(ext);

    /// <summary>
    /// 90 度回転して上書きする。変換し直し（トランスコード）で回転するので、JPEG は可能なら劣化なしで回り、撮影日時などのメタデータは残る。
    /// 一時ファイルに書いてから置き換えるので、途中で失敗しても元のファイルは壊れない（アクセス権・属性も元のまま）。
    /// </summary>
    public static async Task RotateAsync(string path, bool clockwise)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $"~{Path.GetFileNameWithoutExtension(path)}.filebox-rotate{Path.GetExtension(path)}");
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using (var memory = new InMemoryRandomAccessStream())
            {
                using (var input = await file.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await BitmapDecoder.CreateAsync(input);
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(memory, decoder);
                    encoder.BitmapTransform.Rotation = clockwise ? BitmapRotation.Clockwise90Degrees : BitmapRotation.Clockwise270Degrees;
                    await encoder.FlushAsync();
                }
                memory.Seek(0);
                using var output = File.Create(temp);
                await memory.AsStreamForRead().CopyToAsync(output);
            }
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    const uint SPI_SETDESKWALLPAPER = 0x0014;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);

    /// <summary>デスクトップの背景にする（表示のしかた＝フィル・中央などは今の設定のまま）。</summary>
    public static Task SetWallpaperAsync(string path) => Task.Run(() =>
    {
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new InvalidOperationException($"Windows が背景の設定を受け付けませんでした（エラー {Marshal.GetLastWin32Error()}）");
    });
}
