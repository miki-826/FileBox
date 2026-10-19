using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Views;

public static class Dialogs
{
    /// <summary>1行入力ダイアログ。キャンセル時は null。</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string initial, string primary = "OK", bool selectStem = false)
    {
        var box = new TextBox { Text = initial, MinWidth = 360 };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            int dot = initial.LastIndexOf('.');
            if (selectStem && dot > 0) box.Select(0, dot);
            else box.SelectAll();
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    /// <summary>確認ダイアログ。primary を選んだら true。</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "閉じる",
        };
        await dialog.ShowAsync();
    }
}
