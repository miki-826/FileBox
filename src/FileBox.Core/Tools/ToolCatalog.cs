namespace FileBox.Core.Tools;

public enum ToolKind
{
    ImageConvert, ImageResize, ImageCompress, ImageRotate, ImageStripMetadata, ImageRemoveColor, ImageOcr,
    VideoConvert, VideoTrim, VideoCompress, VideoResize, VideoRotate, VideoGif, VideoThumbnail, VideoAudio,
    AudioConvert, PdfMerge, PdfImages, PdfText, CsvConvert, JsonFormat, JsonSort, JsonConvert, ConfigConvert,
    TextEncoding, TextNewlines, ArchiveCreate, ArchiveExtract, FolderTree, JarSources,
}

public sealed record ToolDefinition(ToolKind Kind, string Label, string Group, string[] Extensions, string[] Formats, string? Engine = null)
{
    public bool Accepts(string path) => Directory.Exists(path)
        ? Kind is ToolKind.ArchiveCreate or ToolKind.FolderTree
        : Extensions.Contains("*") || Extensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
}

/// <summary>形式は実装で扱う範囲。実際のコーデックの有無は外部エンジンが検査する。</summary>
public static class ToolCatalog
{
    static readonly string[] Images = ["png", "jpg", "jpeg", "webp", "heic", "heif", "ico", "gif", "svg", "bmp", "tif", "tiff"];
    static readonly string[] Videos = ["mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "mpeg", "mpg", "mts", "m2ts"];
    static readonly string[] Audio = ["mp3", "wav", "aif", "aiff", "aac", "ogg", "wma", "m4a", "flac", "opus"];
    public static readonly ToolDefinition[] All =
    [
        new(ToolKind.ImageConvert, "画像の形式変換", "画像", Images, ["png", "jpg", "webp", "heic", "ico", "gif", "bmp", "tiff"], "magick"),
        new(ToolKind.ImageResize, "画像のリサイズ", "画像", Images, ["png", "jpg", "webp"], "magick"),
        new(ToolKind.ImageCompress, "画像の圧縮", "画像", Images, ["jpg", "webp", "png"], "magick"),
        new(ToolKind.ImageRotate, "画像の回転", "画像", Images, ["png", "jpg", "webp"], "magick"),
        new(ToolKind.ImageStripMetadata, "画像のメタデータを削除", "画像", Images, ["png", "jpg", "webp"], "magick"),
        new(ToolKind.ImageRemoveColor, "画像の背景色を透明にする", "画像", Images, ["png", "webp"], "magick"),
        new(ToolKind.ImageOcr, "画像の文字を読み取る（OCR）", "画像", Images.Where(x => x != "svg").ToArray(), ["txt"]),
        new(ToolKind.VideoConvert, "動画の形式変換", "動画", Videos, ["mp4", "mkv", "webm", "mov"], "ffmpeg"),
        new(ToolKind.VideoTrim, "動画の切り出し", "動画", Videos, ["mp4", "mkv", "webm", "mov"], "ffmpeg"),
        new(ToolKind.VideoCompress, "動画の圧縮", "動画", Videos, ["mp4", "mkv", "webm"], "ffmpeg"),
        new(ToolKind.VideoResize, "動画のリサイズ", "動画", Videos, ["mp4", "mkv", "webm"], "ffmpeg"),
        new(ToolKind.VideoRotate, "動画の回転", "動画", Videos, ["mp4", "mkv", "webm"], "ffmpeg"),
        new(ToolKind.VideoGif, "動画から GIF を作成", "動画", Videos, ["gif"], "ffmpeg"),
        new(ToolKind.VideoThumbnail, "動画から画像を保存", "動画", Videos, ["png", "jpg"], "ffmpeg"),
        new(ToolKind.VideoAudio, "動画から音声を抽出", "動画", Videos, ["mp3", "wav", "m4a", "ogg", "flac"], "ffmpeg"),
        new(ToolKind.AudioConvert, "音声の形式変換", "音声", Audio, Audio, "ffmpeg"),
        new(ToolKind.PdfMerge, "PDF を順番に結合", "文書", ["pdf"], ["pdf"]),
        new(ToolKind.PdfImages, "PDF のページを画像に変換", "文書", ["pdf"], ["png", "jpg"]),
        new(ToolKind.PdfText, "PDF のテキストを抽出", "文書", ["pdf"], ["txt"]),
        new(ToolKind.CsvConvert, "CSV の変換", "データ", ["csv", "tsv"], ["json", "xml", "html", "md"]),
        new(ToolKind.JsonFormat, "JSON の検証と整形", "データ", ["json"], ["json"]),
        new(ToolKind.JsonSort, "JSON のキーを並べ替え", "データ", ["json"], ["json"]),
        new(ToolKind.JsonConvert, "JSON の変換", "データ", ["json"], ["csv", "xml"]),
        new(ToolKind.ConfigConvert, "設定ファイルの変換", "データ", ["ini", "cfg", "conf"], ["json", "xml"]),
        new(ToolKind.TextEncoding, "文字コードの変換", "データ", ["*"], ["txt"]),
        new(ToolKind.TextNewlines, "改行コードの変換", "データ", ["*"], ["txt"]),
        new(ToolKind.ArchiveCreate, "ファイルを圧縮", "整理", ["*"], ["zip", "7z", "tar", "tar.gz"]),
        new(ToolKind.ArchiveExtract, "圧縮ファイルを展開", "整理", ["zip", "7z", "rar", "tar", "gz", "tgz"] , ["folder"]),
        new(ToolKind.FolderTree, "フォルダのツリーを書き出す", "整理", [], ["txt"]),
        new(ToolKind.JarSources, "JAR から Java ソースを抽出", "データ", ["jar"], ["folder"], "java"),
    ];

    public static ToolDefinition Get(ToolKind kind) => All.Single(t => t.Kind == kind);
}

public sealed record ToolOptions
{
    public string Format { get; init; } = "";
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public bool KeepAspect { get; init; } = true;
    public int Quality { get; init; } = 85;
    public double TargetMiB { get; init; }
    public int Angle { get; init; } = 90;
    public bool Crop { get; init; }
    public string Background { get; init; } = "#FFFFFF";
    public int Tolerance { get; init; } = 10;
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public bool FastTrim { get; init; }
    public string Codec { get; init; } = "h264";
    public string Hardware { get; init; } = "cpu";
    public int FrameRate { get; init; } = 12;
    public int Dpi { get; init; } = 150;
    public string Pages { get; init; } = "";
    public string Delimiter { get; init; } = ",";
    public bool HasHeader { get; init; } = true;
    public string InputEncoding { get; init; } = "UTF-8";
    public string OutputEncoding { get; init; } = "UTF-8";
    public string Newline { get; init; } = "LF";
    public int Depth { get; init; } = 5;
    public string OcrLanguage { get; init; } = "";

    public void Validate(ToolKind kind)
    {
        var definition = ToolCatalog.Get(kind);
        if (!definition.Formats.Contains(Format)) throw new ArgumentException("出力形式を選択してください");
        if (Width is < 1 or > 16384 || Height is < 1 or > 16384 || Quality is < 1 or > 100 || Tolerance is < 0 or > 100 ||
            Dpi is < 36 or > 600 || FrameRate is < 1 or > 60 || Depth is < 1 or > 30 || Angle is < -360 or > 360 ||
            !double.IsFinite(TargetMiB) || TargetMiB is < 0 or > 102400 || !double.IsFinite(StartSeconds) || StartSeconds < 0 ||
            !double.IsFinite(EndSeconds) || EndSeconds < 0 || EndSeconds > 0 && EndSeconds <= StartSeconds)
            throw new ArgumentException("数値の範囲または開始・終了位置を確認してください");
        if (kind == ToolKind.VideoTrim && EndSeconds <= StartSeconds) throw new ArgumentException("切り出しの終了位置を指定してください");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Background, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException("背景色は #RRGGBB で指定してください");
        if (Delimiter.Length != 1 || Delimiter[0] is '"' or '\r' or '\n' or '\0') throw new ArgumentException("区切り文字を 1 文字で指定してください");
        if (Newline is not ("LF" or "CRLF" or "CR")) throw new ArgumentException("改行コードを選択してください");
        if (!new[] { "h264", "h265", "vp9", "av1" }.Contains(Codec) || !new[] { "cpu", "nvenc", "qsv", "amf" }.Contains(Hardware))
            throw new ArgumentException("コーデックを選択してください");
        if (kind == ToolKind.VideoRotate && Angle is not (90 or 180 or 270 or -90 or -180 or -270)) throw new ArgumentException("動画の回転は 90 度単位で指定してください");
        if (kind is ToolKind.VideoConvert or ToolKind.VideoTrim or ToolKind.VideoCompress or ToolKind.VideoResize or ToolKind.VideoRotate)
        {
            if (Format == "webm" && Codec is not ("vp9" or "av1")) throw new ArgumentException("WebM は VP9 または AV1 を選択してください");
            if (Codec == "vp9" && Hardware != "cpu") throw new ArgumentException("VP9 は CPU を選択してください");
            if (kind == ToolKind.VideoCompress && TargetMiB > 0 && EndSeconds <= StartSeconds) throw new ArgumentException("目標容量を指定するときは動画の終了秒も指定してください");
        }
    }
}
