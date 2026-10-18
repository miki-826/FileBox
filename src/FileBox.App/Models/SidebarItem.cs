namespace FileBox.App.Models;

public enum SidebarKind { Folder, Favorite, Drive, Workspace, SavedSearch, Inbox, Zone }

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
    public bool IsDrive => Kind == SidebarKind.Drive;
    /// <summary>使用量バーを出す（ドライブ・クォータゾーン）。</summary>
    public bool HasUsage => Kind is SidebarKind.Drive or SidebarKind.Zone;
    public string Tooltip => Kind switch
    {
        SidebarKind.SavedSearch => Query ?? Name,
        SidebarKind.Workspace => $"ワークスペース「{Name}」を開く",
        SidebarKind.Inbox => "ダウンロード・デスクトップに届いたファイルの置き場所を提案",
        _ => Path,
    };
}
