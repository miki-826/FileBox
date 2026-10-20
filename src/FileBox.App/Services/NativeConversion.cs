using FileBox.Core.Tools;
using FileBox.Tools;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileBox.App.Services;

public static class NativeConversion
{
    public static async Task<StagedOutput> ExecuteAsync(string input, ToolKind kind, ToolOptions o, string work, CancellationToken token)
    {
        string stem = Path.GetFileNameWithoutExtension(input);
        if (kind == ToolKind.PdfImages)
        {
            var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(input)).AsTask(token);
            string folder = Path.Combine(work, "pages"); Directory.CreateDirectory(folder);
            foreach (int number in DataConversion.PageRange(o.Pages, (int)pdf.PageCount))
            {
                token.ThrowIfCancellationRequested();
                using var page = pdf.GetPage((uint)(number - 1));
                uint width = (uint)Math.Ceiling(page.Size.Width * o.Dpi / 96), height = (uint)Math.Ceiling(page.Size.Height * o.Dpi / 96);
                if ((long)width * height > 100000000 || width > 16384 || height > 16384) throw new IOException("PDF のページ画像が大きすぎます。解像度を下げてください");
                using var rendered = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height }).AsTask(token);
                string target = Path.Combine(folder, "page-" + number.ToString("D4") + "." + o.Format);
                if (o.Format == "png")
                {
                    rendered.Seek(0); await using var source = rendered.AsStreamForRead(); await using var output = File.Create(target); await source.CopyToAsync(output, token);
                }
                else
                {
                    rendered.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(rendered).AsTask(token);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(token);
                    using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite).AsRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask(token);
                    encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync().AsTask(token);
                }
            }
            return new(folder, stem + "_pages", true);
        }
        if (kind == ToolKind.PdfText)
        {
            const int max = 32000000;
            token.ThrowIfCancellationRequested();
            string? text = IFilterReader.Read(input, max, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new NotSupportedException("PDF のテキストを取得できませんでした。PDF 用 IFilter が必要です。画像だけの PDF はページを画像化して OCR を実行してください");
            if (text.Length >= max) throw new IOException("抽出テキストが上限を超えています。文書を分割してから再実行してください");
            string path = Path.Combine(work, "text.txt"); await File.WriteAllTextAsync(path, text, token);
            return new(path, stem + "_text.txt");
        }
        if (kind == ToolKind.ImageOcr)
        {
            OcrEngine? engine = string.IsNullOrEmpty(o.OcrLanguage) ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(o.OcrLanguage));
            if (engine == null) throw new NotSupportedException("指定言語の OCR パックがありません。Windows の言語設定で OCR の言語パックを追加してください");
            await using var source = File.OpenRead(input);
            using var stream = source.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            uint width = decoder.PixelWidth, height = decoder.PixelHeight;
            double scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(width, height));
            var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(width * scale)), ScaledHeight = Math.Max(1, (uint)(height * scale)) };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
            var recognized = await engine.RecognizeAsync(bitmap).AsTask(token);
            string path = Path.Combine(work, "ocr.txt"); await File.WriteAllTextAsync(path, recognized.Text, token);
            return new(path, stem + "_ocr.txt", Notice: scale < 1 ? "OCR の上限に合わせて画像を縮小して読み取りました。" : null);
        }
        throw new NotSupportedException();
    }
}
