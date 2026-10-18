using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FileBox.App.Models;

namespace FileBox.App.ViewModels;

/// <summary>プレビュー区画。LinkedTo の区画で選択された項目を表示する（02_機能設計 §2.2）。</summary>
public sealed partial class PreviewPaneViewModel : PaneViewModel
{
    public override PaneKind Kind => PaneKind.Preview;

    /// <summary>連動元の区画 ID。null ならタブ内で最後に操作した区画に連動する。</summary>
    [ObservableProperty]
    string? _linkedTo;

    [ObservableProperty]
    FileItem? _item;

    public PreviewPaneViewModel(string? linkedTo, string? id = null)
    {
        if (id != null) Id = id;
        LinkedTo = linkedTo;
        Title = "プレビュー";
    }

    partial void OnItemChanged(FileItem? value) => Title = value == null ? "プレビュー" : $"プレビュー: {value.Name}";

    public override JsonObject Serialize() => new()
    {
        ["pane"] = "preview",
        ["id"] = Id,
        ["linkedTo"] = LinkedTo,
    };
}
