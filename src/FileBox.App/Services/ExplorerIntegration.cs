using System.Diagnostics;
using System.Runtime.InteropServices;
using FileBox.Data;
using Microsoft.Win32;

namespace FileBox.App.Services;

/// <summary>
/// エクスプローラー置き換え（02_機能設計 §7）。HKCU だけを変更し、変更前の値を user.db に退避して
/// オフにしたときに完全に元へ戻す。対象は実在のフォルダ（Directory）とドライブ（Drive）だけで、
/// ごみ箱・コントロールパネルなどの仮想フォルダ（Folder）はエクスプローラーのまま。
/// </summary>
public sealed class ExplorerIntegration : IDisposable
{
    static ExplorerIntegration? _instance;
    public static ExplorerIntegration Instance => _instance ?? throw new InvalidOperationException("ExplorerIntegration.Initialize が未実行");

    const string Verb = "FileBox";
    static readonly string[] Classes = { "Directory", "Drive" };

    readonly UserStore _store;
    WinEHook? _hook;

    ExplorerIntegration(UserStore store) => _store = store;

    public static void Initialize(UserStore store, Action onWinE)
    {
        if (_instance != null) return;
        _instance = new ExplorerIntegration(store);
        if (_instance.IsEnabled)
        {
            try { _instance.WriteVerbs(); } catch (Exception ex) { Debug.WriteLine(ex); }   // 実行ファイルの場所が変わっていても追従
        }
        _instance._onWinE = onWinE;
        if (_instance.WinEEnabled) _instance.StartHook();
    }

    Action? _onWinE;

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
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileBox.exe");
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

    // ================================================================ Win+E（§7.1）

    public void SetWinE(bool on)
    {
        _store.SetSetting("explorer.winE", on ? "1" : "0");
        if (on) StartHook(); else StopHook();
    }

    void StartHook()
    {
        if (_hook != null || _onWinE == null) return;
        _hook = new WinEHook(_onWinE);
        if (!_hook.Install()) _hook = null;
    }

    void StopHook()
    {
        _hook?.Dispose();
        _hook = null;
    }

    public void Dispose() => StopHook();

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

/// <summary>
/// Win+E を FileBox で受ける低レベルキーボードフック。FileBox 起動中だけ有効（UI スレッドで設置する）。
/// </summary>
sealed class WinEHook : IDisposable
{
    readonly Action _onWinE;
    readonly LowLevelKeyboardProc _proc;   // GC されないよう保持
    IntPtr _hook;

    public WinEHook(Action onWinE)
    {
        _onWinE = onWinE;
        _proc = Callback;
    }

    public bool Install()
    {
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            if (vk == 'E' && win)
            {
                // フック内では重い処理をしない（遅いと OS にフックを外される）
                App.Window?.DispatcherQueue.TryEnqueue(() => _onWinE());
                // Win キーだけが押されたと見なされてスタートメニューが開くのを防ぐため、未割り当てのキーを1回送る
                keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    const int WH_KEYBOARD_LL = 13;
    static readonly IntPtr WM_KEYDOWN = 0x0100;
    static readonly IntPtr WM_SYSKEYDOWN = 0x0104;
    const int VK_LWIN = 0x5B;
    const int VK_RWIN = 0x5C;

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
