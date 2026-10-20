using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FileBox.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // アンインストーラーから: Windows に加えた登録を元に戻して終わる（画面は作らない）
        if (args.Length > 0 && args[0] == "--unregister-shell")
        {
            try
            {
                using var store = new Data.UserStore();
                Services.ExplorerIntegration.RevertAll(store);
                Services.ConversionContextMenu.Apply(store, false);
            }
            catch (Exception) { return 1; }
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 単一インスタンス: 2つ目以降の起動は既存ウィンドウへ引数を渡して終了（01_アーキテクチャ §5）
        var main = AppInstance.FindOrRegisterForKey("FileBox.Main");
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var redirect = Task.Run(() => main.RedirectActivationToAsync(activation).AsTask());
            // 起動中の FileBox が応答しなければ、フォルダはエクスプローラーで開く（02_機能設計 §7.3）
            if (!redirect.Wait(TimeSpan.FromSeconds(2))) FallbackToExplorer(args);
            return 0;
        }
        main.Activated += (_, e) => App.OnRedirectedActivation(e);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    static void FallbackToExplorer(string[] args)
    {
        // Win + E からの起動は「PC」を開く（Win + E と同じ動詞を通すと FileBox に戻ってくるため、場所を直接渡す）
        if (args.Contains("--home")) args = new[] { "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" };
        foreach (var arg in args.Where(a => Directory.Exists(a) || a.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)))
        {
            // ShellExecute で開くと置き換えた動詞で FileBox がまた起動するので、エクスプローラーに直接渡す
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{arg}\""); }
            catch (Exception) { }
        }
    }
}
