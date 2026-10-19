using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;

namespace FileBox.App.ViewModels;

public enum PaneKind { Folder, Preview, Search, Inbox, Capacity, Admin, Shell }

/// <summary>分割ペイン1区画の ViewModel（02_機能設計 §2.1）。</summary>
public abstract partial class PaneViewModel : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public abstract PaneKind Kind { get; }

    [ObservableProperty]
    string _title = "";

    /// <summary>この区画で選択中の項目が変わった（連動プレビュー用）。</summary>
    public event Action<PaneViewModel, FileItem?>? SelectionChanged;

    protected void RaiseSelectionChanged(FileItem? item) => SelectionChanged?.Invoke(this, item);

    public abstract JsonObject Serialize();

    public static PaneViewModel? Deserialize(JsonObject node)
    {
        string? kind = (string?)node["pane"];
        string? id = (string?)node["id"];
        PaneViewModel? pane = kind switch
        {
            "folder" => new FolderPaneViewModel((string?)node["path"] ?? "", id),
            "preview" => new PreviewPaneViewModel((string?)node["linkedTo"], id),
            "search" => new SearchPaneViewModel((string?)node["query"] ?? "", id),
            "inbox" => new InboxPaneViewModel(id),
            "capacity" => new CapacityPaneViewModel((string?)node["path"], id),
            "admin" => new AdminPaneViewModel(id),
            "shell" => new ShellPaneViewModel((string?)node["location"] ?? ShellPaneViewModel.Recycle, id),
            _ => null,
        };
        return pane;
    }

    public virtual void Dispose() { }
}
