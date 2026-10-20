using System.Security.Cryptography;
using FileBox.App.Services;
using FileBox.Core.Files;
using FileBox.Core.Security;
using FileBox.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileBox.App.Views;

public static class OrganizeToolsDialog
{
    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<string> paths)
    {
        var actions = new ComboBox { Header = "整理の方法", MinWidth = 400 };
        foreach (string label in new[] { "重複ファイルを探す", "空フォルダを探す", "名前を一括で変更", "容量の内訳を見る" }) actions.Items.Add(label);
        actions.SelectedIndex = 0;
        var scope = new TextBlock { Text = string.Join("\n", paths), Tag = "data", TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
        var form = new StackPanel { Spacing = 10, MinWidth = 480 };
        form.Children.Add(new TextBlock { Text = "対象の場所", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); form.Children.Add(scope); form.Children.Add(actions);
        var dialog = new ContentDialog { XamlRoot = root, Title = "ファイルとフォルダの整理", Content = form, PrimaryButtonText = "次へ", CloseButtonText = "キャンセル" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (actions.SelectedIndex == 2) { await BatchRenameDialog.ShowAsync(root, paths); return; }
        var folders = paths.Where(Directory.Exists).ToList();
        if (folders.Count == 0) { App.Window?.ShowInfo("対象のフォルダを選択してください", InfoBarSeverity.Warning); return; }
        if (folders.Any(p => !Guard.Can(Permission.Read, p, "inspect"))) return;
        if (actions.SelectedIndex == 3) { App.Window?.OpenCapacity(folders[0]); return; }
        using var cts = new CancellationTokenSource();
        bool running = true;
        var status = new TextBlock { Text = "探しています…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true };
        var scanDialog = new ContentDialog { XamlRoot = root, Title = (string)actions.SelectedItem, Content = new StackPanel { Spacing = 12, Children = { status, progress } }, CloseButtonText = "中止" };
        scanDialog.CloseButtonClick += (_, e) => { if (running) { e.Cancel = true; cts.Cancel(); status.Text = "停止しています…"; } };
        scanDialog.Closing += (_, e) => { if (running) { e.Cancel = true; cts.Cancel(); } };
        var shown = scanDialog.ShowAsync();
        IReadOnlyList<DuplicateGroup> duplicates = Array.Empty<DuplicateGroup>();
        var empty = new List<string>(); var unreadable = new List<string>();
        try
        {
            if (actions.SelectedIndex == 0)
                duplicates = await DuplicateFinder.FindAsync(folders, minSize: 0, progress: new Progress<DuplicateProgress>(p => status.Text = $"{p.Stage} {p.Done:N0} / {p.Total:N0}"), ct: cts.Token,
                    canRead: p => PolicyService.Instance.Can(Permission.Read, p));
            else
                foreach (string folder in folders)
                {
                    var report = await Task.Run(() => FolderInspection.EmptyFolders(folder, cts.Token, p => PolicyService.Instance.Can(Permission.Read, p)), cts.Token);
                    empty.AddRange(report.Paths); unreadable.AddRange(report.Unreadable);
                }
        }
        catch (OperationCanceledException) { status.Text = "中止しました"; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { running = false; progress.IsIndeterminate = false; scanDialog.CloseButtonText = "結果を確認"; }
        scanDialog.Hide(); await shown;
        if (cts.IsCancellationRequested) return;
        if (actions.SelectedIndex == 0) await DuplicatesAsync(root, duplicates);
        else await EmptyFoldersAsync(root, empty.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), unreadable);
    }

    static async Task DuplicatesAsync(XamlRoot root, IReadOnlyList<DuplicateGroup> groups)
    {
        var content = new StackPanel { Spacing = 16, MinWidth = 500 };
        content.Children.Add(Note($"重複 {groups.Count} 組。各組で残すファイルを選択し、整理する組だけにチェックを入れてください。削除はごみ箱へ移動します。"));
        var choices = new List<(CheckBox Check, DuplicateGroup Group, List<(RadioButton Radio, string Path)> Files)>();
        foreach (var group in groups)
        {
            var check = new CheckBox { Content = $"この組を整理する（{Models.Format.HumanSize(group.Wasted)}）", IsChecked = false };
            content.Children.Add(check); string name = Guid.NewGuid().ToString(); var files = new List<(RadioButton Radio, string Path)>();
            foreach (string path in group.Paths)
            {
                var radio = new RadioButton { GroupName = name, IsChecked = files.Count == 0, Content = new TextBlock { Text = path, Tag = "data", TextWrapping = TextWrapping.Wrap } };
                files.Add((radio, path)); content.Children.Add(radio);
            }
            choices.Add((check, group, files));
        }
        var dialog = new ContentDialog { XamlRoot = root, Title = "重複ファイルの確認", Content = new ScrollViewer { Content = content, MaxHeight = 580 }, PrimaryButtonText = "対象を確認", CloseButtonText = "閉じる", IsPrimaryButtonEnabled = groups.Count > 0 };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selected = choices.Where(c => c.Check.IsChecked == true).ToList();
        var deleting = selected.SelectMany(c => c.Files.Where(f => f.Radio.IsChecked != true).Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (deleting.Count == 0) return;
        string retained = string.Join("\n", selected.SelectMany(c => c.Files.Where(f => f.Radio.IsChecked == true).Select(f => f.Path)));
        if (!await ConfirmPathsAsync(root, "重複ファイルをごみ箱へ移動", deleting, "残すファイル\n" + retained)) return;
        // プレビュー後に変わったファイルを消さないよう、読み取りロックして再比較する。
        var locks = new List<FileStream>();
        try
        {
            foreach (var choice in selected)
            {
                foreach (var file in choice.Files)
                {
                    if (!Guard.Can(Permission.Read, file.Path, "inspect")) throw new UnauthorizedAccessException("読み取りが許可されていません");
                    var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    locks.Add(stream);
                    string hash = await Task.Run(() => Convert.ToHexString(SHA256.HashData(stream)));
                    if (hash != choice.Group.Hash || stream.Length != choice.Group.Size) throw new IOException("検出後にファイルが変更されました。もう一度重複を検索してください");
                }
            }
            FileOperations.Instance.Recycle(deleting);
        }
        catch (Exception ex) { App.Window?.ShowInfo(ex.Message, InfoBarSeverity.Warning); }
        finally { foreach (var file in locks) file.Dispose(); }
    }

    static async Task EmptyFoldersAsync(XamlRoot root, IReadOnlyList<string> paths, IReadOnlyList<string> unreadable)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 500 };
        panel.Children.Add(Note($"空フォルダ {paths.Count} 件。読み取り不可 {unreadable.Count} 件。配下を再帰検索し、リンクは辿りません。選択した起点は削除しません。"));
        panel.Children.Add(Note("空であることを削除直前に再確認し、空フォルダだけを完全削除します。ごみ箱には移動しません。中に項目が増えた場合は削除しません。"));
        var checks = new List<(CheckBox Check, string Path)>();
        foreach (string path in paths)
        {
            var check = new CheckBox { Content = new TextBlock { Text = path, Tag = "data", TextWrapping = TextWrapping.Wrap }, IsChecked = false };
            checks.Add((check, path)); panel.Children.Add(check);
        }
        var dialog = new ContentDialog { XamlRoot = root, Title = "空フォルダの確認", Content = new ScrollViewer { Content = panel, MaxHeight = 580 }, PrimaryButtonText = "対象を確認", CloseButtonText = "閉じる", IsPrimaryButtonEnabled = paths.Count > 0 };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var targets = checks.Where(c => c.Check.IsChecked == true).Select(c => c.Path).ToList();
        if (targets.Count == 0 || !await ConfirmPathsAsync(root, "空フォルダを完全削除", targets, "元に戻せません。中にファイルがあるフォルダは削除しません。")) return;
        int done = 0; var failures = new List<string>();
        foreach (string path in targets)
        {
            try
            {
                if (!Guard.Can(Permission.Delete, path, "delete")) { failures.Add(path); continue; }
                SafeOutput.RejectReparseParents(path);
                Directory.Delete(path, recursive: false); Guard.Done("delete", path, detail: "確認済みの空フォルダのみ削除"); done++;
            }
            catch (Exception ex) { failures.Add(path + ": " + ex.Message); }
        }
        await new ContentDialog { XamlRoot = root, Title = $"削除 {done} 件・スキップ {failures.Count} 件", Content = new ScrollViewer { Content = Note(string.Join("\n", failures)), MaxHeight = 400 }, CloseButtonText = "閉じる" }.ShowAsync();
    }

    static async Task<bool> ConfirmPathsAsync(XamlRoot root, string title, IReadOnlyList<string> paths, string note)
    {
        var text = new TextBox { Text = note + $"\n\n対象 {paths.Count} 件\n" + string.Join("\n", paths), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 500, MaxHeight = 500 };
        return await new ContentDialog { XamlRoot = root, Title = title, Content = text, PrimaryButtonText = "実行", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    }
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
}
