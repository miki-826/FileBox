using FileBox.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Views;

/// <summary>区画のビューからメインウィンドウ（タブ・分割）へ操作を依頼するための口。</summary>
public interface IWorkspaceActions
{
    void ActivatePane(PaneViewModel pane);
    void OpenInNewTab(string path);
    void OpenInAdjacentPane(PaneViewModel from, string path);
    void TogglePreview(PaneViewModel from);
    void SplitPane(PaneViewModel pane, Orientation orientation);
    void ClosePane(PaneViewModel pane);
    /// <summary>タブ内のフォルダ区画で path を開く（ファイルなら親フォルダを開いて選択）。</summary>
    void RevealInFolderPane(PaneViewModel from, string path);
    void AddFavorite(string path);
    void OpenInbox();
}
