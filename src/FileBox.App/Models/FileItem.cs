using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileBox.App.Models;

/// <summary>一覧・検索結果の1行。</summary>
public sealed class FileItem : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public bool IsHidden { get; init; }
    long? _folderBytes;
    bool _folderSizePartial;
    string _folderSizeState = "未集計";
    public long? FolderBytes => _folderBytes;
    public long SortSize => IsDirectory && !IsDrive ? _folderBytes ?? -1 : Size;

    public void SetFolderSize(long? bytes, bool partial = false, string? state = null)
    {
        _folderBytes = bytes;
        _folderSizePartial = partial;
        _folderSizeState = state ?? "未集計";
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HumanSize));
        OnPropertyChanged(nameof(SizeDescription));
    }
    public string SizeDescription => IsDirectory && !IsDrive
        ? _folderBytes is long bytes ? $"配下のファイル合計: {bytes:N0} バイト" + (_folderSizePartial ? "（集計途中または一部取得不可）" : "（索引または読み取れたファイルに基づく）") : _folderSizeState
        : SizeText;

    /// <summary>ドライブの使用率（0〜1）とラベル。</summary>
    public double DriveUsage { get; init; }
    public string? DriveInfoText { get; init; }

    public string Glyph => IsDrive ? "" : IsDirectory ? "" : FileKinds.Glyph(Extension);
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
    public string TypeText => IsDrive ? "ドライブ" : IsDirectory ? "ファイル フォルダー" : Extension.Length > 1 ? $"{Extension[1..].ToUpperInvariant()} ファイル" : "ファイル";
    public string SizeText => IsDrive ? DriveInfoText ?? "" : IsDirectory
        ? _folderBytes is long bytes ? _folderSizePartial && bytes == 0 ? "未集計" : (_folderSizePartial ? "約 " : "") + Format.HumanSize(bytes) : _folderSizeState
        : Format.HumanSize(Size);
    public string HumanSize => IsDirectory ? SizeText : Format.HumanSize(Size);
    public string ModifiedText => Modified?.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) ?? "";
    public string Location => Path.GetDirectoryName(FullPath) ?? "";
    public double ItemOpacity => IsHidden ? 0.6 : 1.0;

    public static FileItem FromInfo(FileSystemInfo info) => new()
    {
        Name = info.Name,
        FullPath = info.FullName,
        IsDirectory = info is DirectoryInfo,
        Size = info is FileInfo f ? f.Length : 0,
        Modified = info.LastWriteTime,
        IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
    };
}

/// <summary>一覧のまとまり（日付でグループ化したときの「今日」「先週」など）。</summary>
public sealed class ItemGroup(string header, IEnumerable<object> items) : List<object>(items)
{
    public string Header { get; } = header;
}

public static class FileKinds
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".jfif" };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v", ".webm" };
    static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma", ".ogg" };
    static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".cab" };
    static readonly HashSet<string> Web = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".html", ".htm", ".svg", ".mhtml" };
    static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".hpp", ".css", ".scss",
        ".json", ".xml", ".xaml", ".yaml", ".yml", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".csproj", ".sln", ".toml",
    };
    static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".log", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".gitignore", ".editorconfig" };
    static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".lnk", ".com", ".scr" };

    public static bool IsImage(string ext) => Images.Contains(ext);
    public static bool IsVideo(string ext) => Videos.Contains(ext);
    public static bool IsAudio(string ext) => Audio.Contains(ext);
    public static bool IsWeb(string ext) => Web.Contains(ext);
    public static bool IsText(string ext) => Text.Contains(ext) || Code.Contains(ext);
    public static bool IsArchive(string ext) => Archives.Contains(ext);

    static readonly HashSet<string> Word = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".docm", ".rtf", ".odt" };
    static readonly HashSet<string> Excel = new(StringComparer.OrdinalIgnoreCase) { ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv", ".ods" };
    static readonly HashSet<string> PowerPoint = new(StringComparer.OrdinalIgnoreCase) { ".ppt", ".pptx", ".pptm", ".odp" };

    /// <summary>種類ごとのアイコンの色（#RRGGBB）。見分けやすさのため Office などは各アプリの色に寄せる。null は既定の文字色。</summary>
    public static string? Color(string ext) =>
        ext == ".pdf" ? "#D93025" :
        Word.Contains(ext) ? "#2B579A" :
        Excel.Contains(ext) ? "#217346" :
        PowerPoint.Contains(ext) ? "#C43E1C" :
        Images.Contains(ext) ? "#0F9D8C" :
        Videos.Contains(ext) ? "#C2185B" :
        Audio.Contains(ext) ? "#8E4EC6" :
        Archives.Contains(ext) ? "#B7791F" :
        Web.Contains(ext) ? "#E44D26" :
        Code.Contains(ext) ? "#3E63DD" :
        Executables.Contains(ext) ? "#5B6B7F" :
        Text.Contains(ext) ? "#6E7781" :
        null;

    public static string Glyph(string ext) =>
        Images.Contains(ext) ? "" :
        Videos.Contains(ext) ? "" :
        Audio.Contains(ext) ? "" :
        Archives.Contains(ext) ? "" :
        Code.Contains(ext) ? "" :
        Executables.Contains(ext) ? "" :
        ext == ".pdf" ? "" :
        "";
}

public static class Format
{
    /// <summary>エクスプローラーと同じ「切り上げ KB」表記。</summary>
    public static string ExplorerSize(long bytes) =>
        bytes == 0 ? "0 KB" : $"{(bytes + 1023) / 1024:N0} KB";

    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
