using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Services;

namespace FileBox.App.ViewModels;

/// <summary>受け取りボックス区画（02_機能設計 §1.6）。中身は全区画で共通の <see cref="InboxService"/>。</summary>
public sealed partial class InboxPaneViewModel : PaneViewModel
{
    public override PaneKind Kind => PaneKind.Inbox;

    public ObservableCollection<InboxItem> Items => InboxService.Instance.Items;

    [ObservableProperty]
    string _summary = "";

    /// <summary>項目が増えた・減った（日付ごとのまとまりを作り直す）。</summary>
    public event Action? ItemsChanged;

    public InboxPaneViewModel(string? id = null)
    {
        if (id != null) Id = id;
        Title = "受け取りボックス";
        InboxService.Instance.CountChanged += UpdateSummary;
        Items.CollectionChanged += OnItemsChanged;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int confident = Items.Count(i => i.Confidence == Core.Suggest.Confidence.High && i.TargetPath != null && !i.TargetIsNew);
        Summary = Items.Count == 0
            ? "未整理のファイルはありません"
            : $"未整理 {Items.Count} 件" + (confident > 0 ? $"（うち確信度「高」{confident} 件）" : "");
    }

    public void Refresh() => UpdateSummary();

    public void OnSelectionChanged(Models.FileItem? item) => RaiseSelectionChanged(item);

    public override JsonObject Serialize() => new() { ["pane"] = "inbox", ["id"] = Id };

    void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ItemsChanged?.Invoke();

    public override void Dispose()
    {
        InboxService.Instance.CountChanged -= UpdateSummary;
        Items.CollectionChanged -= OnItemsChanged;
    }
}
