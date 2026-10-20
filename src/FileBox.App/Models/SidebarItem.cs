namespace FileBox.App.Models;

public enum SidebarKind { Folder, Favorite, Drive, Workspace, SavedSearch, Inbox, Zone, Recent, Shell, Tag, FrequentSearch }

public sealed class SidebarItem
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public SidebarKind Kind { get; init; }
    public string Path { get; init; } = "";
    public long Id { get; init; }
    public string? Query { get; init; }
    public double Usage { get; init; }
    public string? Detail { get; init; }
    /// <summary>アイコンの色（#RRGGBB）。null は既定の文字色。</summary>
    public string? Color { get; init; }
    public bool IsDrive => Kind == SidebarKind.Drive;
    /// <summary>使用量バーを出す（ドライブ・クォータゾーン）。</summary>
    public bool HasUsage => Kind is SidebarKind.Drive or SidebarKind.Zone;
    /// <summary>読み上げ（UI オートメーション）で項目名が読まれるように。</summary>
    public override string ToString() => Name;

    public string Tooltip => Kind switch
    {
        SidebarKind.Drive => $"{Path}\n{Detail}",
        SidebarKind.Zone => $"{Path}\n{Detail}",
        SidebarKind.Folder when Path.Length == 0 => "PC のドライブを表示",
        SidebarKind.SavedSearch => Query ?? Name,
        SidebarKind.FrequentSearch => $"よく使う検索: {Query}（右クリックで保存）",
        SidebarKind.Tag => Query == "is:starred" ? "星を付けた項目を表示" : $"タグ「{Name}」を付けた項目を表示",
        SidebarKind.Workspace => $"ワークスペース「{Name}」を開く",
        SidebarKind.Inbox => "ダウンロード・デスクトップに届いたファイルの置き場所を提案",
        SidebarKind.Shell => $"{Name}を FileBox で開く",
        _ => Path,
    };
}
