using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace FileBox.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        // WebView2（PDF/HTML プレビュー）のデータは実行フォルダではなくユーザー領域に置く
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileBox", "WebView2"));
        UnhandledException += (_, e) =>
        {
            ShowError("予期しないエラーが発生しました", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 言語とアクセント色は、ウィンドウを作る前に読み込む（作った後では反映されない部分があるため）
        try
        {
            using var store = new Data.UserStore();
            Services.Text.Initialize(store);
            Services.ThemeService.ApplyAccent(this, Services.ThemeService.Accent(store));
        }
        catch (Exception ex) { Services.AppLog.Error("表示の設定を読み込めませんでした", ex); }
        Window = new MainWindow();
        Window.OpenFromArguments(Environment.GetCommandLineArgs().Skip(1));
        Window.Activate();
    }

    /// <summary>2つ目の起動（エクスプローラー置き換え・コマンドライン）から渡された引数で既存ウィンドウを開く。</summary>
    public static void OnRedirectedActivation(AppActivationArguments args)
    {
        var window = Window;
        if (window == null) return;
        string commandLine = args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch ? launch.Arguments : "";
        var parts = SplitCommandLine(commandLine);
        string self = Environment.ProcessPath ?? "";
        // 先頭の実行ファイル自身を除く（filebox:// リンクなどパスでない引数もあるので GetFullPath は使わない）
        var paths = parts.Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("FileBox.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.OpenFromArguments(paths);
            window.Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        });
    }

    static List<string> SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return new();
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        try
        {
            var list = new List<string>();
            for (int i = 0; i < argc; i++) list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!);
            return list;
        }
        finally { LocalFree(argv); }
    }

    static bool _showingError;

    /// <summary>
    /// エラーを、原因と対処が分かる言葉で表示する（例外のメッセージは「詳しい情報」に畳む）。
    /// path を渡すと、使用中のときにどのアプリが使っているかも調べて添える。
    /// </summary>
    public static void ShowError(string message, Exception? ex = null, string? path = null)
    {
        Services.AppLog.Error(path == null ? message : $"{message} ({path})", ex);
        var window = Window;
        if (window?.Content?.XamlRoot == null || _showingError) return;
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_showingError) return;
            _showingError = true;
            try
            {
                var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
                if (ex != null)
                {
                    var explained = Core.Files.FriendlyError.Explain(ex);
                    panel.Children.Add(new TextBlock { Text = explained.Cause, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    if (explained.Action.Length > 0) panel.Children.Add(new TextBlock { Text = explained.Action, TextWrapping = TextWrapping.Wrap });
                    if (explained.IsInUse && path != null)
                    {
                        var apps = await Task.Run(() => Services.FileLocks.WhoIsUsing(path));
                        if (apps.Count > 0) panel.Children.Add(new TextBlock { Text = $"使っているアプリ: {string.Join("、", apps)}", TextWrapping = TextWrapping.Wrap });
                    }
                    panel.Children.Add(new Expander
                    {
                        Header = "詳しい情報",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = new TextBlock { Text = $"{ex.Message}\n(0x{ex.HResult:X8} {ex.GetType().Name})", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 },
                    });
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = window.Content.XamlRoot,
                    Title = message,
                    Content = panel,
                    CloseButtonText = "閉じる",
                };
                await dialog.ShowAsync();
            }
            catch (Exception e) { Services.AppLog.Error("エラーの表示に失敗しました", e); }
            finally { _showingError = false; }
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
}
