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

    public static void ShowError(string message, Exception? ex = null)
    {
        System.Diagnostics.Debug.WriteLine($"{message}: {ex}");
        var window = Window;
        if (window?.Content?.XamlRoot == null || _showingError) return;
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (_showingError) return;
            _showingError = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = window.Content.XamlRoot,
                    Title = message,
                    Content = new TextBlock { Text = ex?.Message ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    CloseButtonText = "閉じる",
                };
                await dialog.ShowAsync();
            }
            catch (Exception) { }
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
