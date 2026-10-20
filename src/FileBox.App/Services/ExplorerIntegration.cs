using System.Diagnostics;
using System.Runtime.InteropServices;
using FileBox.Data;
using Microsoft.Win32;

namespace FileBox.App.Services;

/// <summary>
/// エクスプローラー置き換え（02_機能設計 §7）。HKCU だけを変更し、変更前の値を user.db に退避して
/// オフにしたときに完全に元へ戻す。対象は実在のフォルダ（Directory）とドライブ（Drive）だけで、
/// ごみ箱・コントロールパネルなどの仮想フォルダ（Folder）はエクスプローラーのまま。
/// Win + E とタスクバーのエクスプローラーのボタンは、「PC」の新しいウィンドウを開く動詞を HKCU に置いて受ける。
/// </summary>
public sealed class ExplorerIntegration
{
    static ExplorerIntegration? _instance;
    public static ExplorerIntegration Instance => _instance ?? throw new InvalidOperationException("ExplorerIntegration.Initialize が未実行");

    const string Verb = "FileBox";
    static readonly string[] Classes = { "Directory", "Drive" };

    readonly UserStore _store;

    ExplorerIntegration(UserStore store) => _store = store;

    public static void Initialize(UserStore store)
    {
        if (_instance != null) return;
        _instance = new ExplorerIntegration(store);
        // 実行ファイルの場所が変わっていても追従する
        if (_instance.IsEnabled)
        {
            try { _instance.WriteVerbs(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }
        if (_instance.WinEEnabled)
        {
            try { _instance.SetWinE(true); } catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    public bool IsEnabled => _store.GetSetting("explorer.replace") == "1";
    public bool WinEEnabled => _store.GetSetting("explorer.winE") == "1";

    // ================================================================ フォルダを開く導線（§7.1）

    public void Enable()
    {
        foreach (var cls in Classes)
        {
            string shellKey = $@"Software\Classes\{cls}\shell";
            using var key = Registry.CurrentUser.OpenSubKey(shellKey);
            object? old = key?.GetValue("");
            _store.BackupRegistryValue(shellKey, "", old as string, old != null);
        }
        WriteVerbs();
        _store.SetSetting("explorer.replace", "1");
        NotifyShell();
    }

    void WriteVerbs()
    {
        string exe = Exe;
        foreach (var cls in Classes)
        {
            using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell");
            shell.SetValue("", Verb);
            using var verb = shell.CreateSubKey(Verb);
            verb.SetValue("", "FileBox で開く");
            verb.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }

    /// <summary>置き換えをやめ、レジストリを変更前に戻す（§7.3）。</summary>
    public void Disable()
    {
        foreach (var cls in Classes)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{cls}\shell\{Verb}", throwOnMissingSubKey: false); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        foreach (var (keyPath, valueName, value, existed) in _store.GetRegistryBackups())
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                if (existed && value != null) key.SetValue(valueName, value);
                else key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        // 既定値を戻したあと、FileBox が作っただけの空キーは消す
        foreach (var cls in Classes)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell");
                if (shell != null && shell.SubKeyCount == 0 && shell.ValueCount == 0)
                    Registry.CurrentUser.DeleteSubKey($@"Software\Classes\{cls}\shell", throwOnMissingSubKey: false);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        _store.ClearRegistryBackups();
        _store.SetSetting("explorer.replace", "0");
        NotifyShell();
    }

    /// <summary>現在のレジストリが FileBox を既定にしているか（他のツールに上書きされていないか）。</summary>
    public bool IsActiveInRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell");
        return key?.GetValue("") as string == Verb;
    }

    static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    // ================================================================ Win+E とタスクバーのエクスプローラー（§7.1）

    // Win + E とタスクバーのエクスプローラーのボタンは、「PC」の opennewwindow 動詞で新しいウィンドウを開く。
    // HKCU にこの動詞を置くと、FileBox が起動していなくても FileBox が開く（HKLM の既定は変えない）
    const string HomeClsid = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
    const string HomeCommand = HomeClsid + @"\shell\opennewwindow\command";

    public void SetWinE(bool on)
    {
        if (on)
        {
            if (_store.GetSetting("explorer.winE.backup") is not { Length: > 0 }) BackupHome();
            WriteHome();
            _store.SetSetting("explorer.winE", "1");
        }
        else
        {
            RestoreHome();
            _store.SetSetting("explorer.winE", "0");
        }
    }

    /// <summary>Win + E が FileBox を開くようになっているか（他のツールに上書きされていないか）。</summary>
    public bool IsWinEActiveInRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(HomeCommand);
        return key?.GetValue("") is string cmd && cmd.Contains("--home", StringComparison.Ordinal) && cmd.Contains("FileBox", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>変更前の値を「有無|既定値|DelegateExecute」で記録する（HKCU には普通は無い）。</summary>
    void BackupHome()
    {
        using var key = Registry.CurrentUser.OpenSubKey(HomeCommand);
        string backup = key == null ? "0" : $"1|{key.GetValue("") as string}|{key.GetValue("DelegateExecute") as string}";
        _store.SetSetting("explorer.winE.backup", backup);
    }

    void WriteHome()
    {
        using var cmd = Registry.CurrentUser.CreateSubKey(HomeCommand);
        cmd.SetValue("", $"\"{Exe}\" --home");
        // 空の DelegateExecute があると、既定の COM の処理ではなく上のコマンドが使われる
        cmd.SetValue("DelegateExecute", "");
    }

    void RestoreHome()
    {
        string[] parts = (_store.GetSetting("explorer.winE.backup") ?? "0").Split('|');
        try
        {
            if (parts.Length >= 3 && parts[0] == "1")
            {
                using var cmd = Registry.CurrentUser.CreateSubKey(HomeCommand);
                cmd.SetValue("", parts[1]);
                cmd.SetValue("DelegateExecute", parts[2]);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(HomeClsid + @"\shell\opennewwindow", throwOnMissingSubKey: false);
                // FileBox が作っただけの空のキーは消す
                foreach (var path in new[] { HomeClsid + @"\shell", HomeClsid })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(path);
                    if (key != null && key.SubKeyCount == 0 && key.ValueCount == 0) Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        _store.SetSetting("explorer.winE.backup", "");
    }

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileBox.exe");

    /// <summary>
    /// アンインストール時（FileBox.exe --unregister-shell）。FileBox が Windows に加えた登録をすべて変更前に戻す。
    /// 画面は作らずに終わる。
    /// </summary>
    public static void RevertAll(UserStore store)
    {
        var integration = new ExplorerIntegration(store);
        try { if (integration.IsEnabled || integration.IsActiveInRegistry()) integration.Disable(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { if (integration.WinEEnabled || integration.IsWinEActiveInRegistry()) integration.SetWinE(false); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
