using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FileBox.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
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
        foreach (var arg in args.Where(Directory.Exists))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(arg) { UseShellExecute = true, Verb = "open" }); }
            catch (Exception) { }
        }
    }
}
