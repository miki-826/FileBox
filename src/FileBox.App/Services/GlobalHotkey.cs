using System.Runtime.InteropServices;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// どこからでも FileBox の検索を呼ぶホットキー（既定はオフ。設定でオンにしたときだけ OS に登録する）。
/// FileBox が起動している間だけ有効で、終了すると登録は消える。ほかのアプリが同じキーを使っていれば登録に失敗する。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    const int HotkeyId = 0xF1B0;
    const uint WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    /// <summary>選べるキーの組み合わせ（表示名, 修飾キー, 仮想キーコード）。</summary>
    public static readonly (string Label, uint Mods, uint Key)[] Choices =
    {
        ("Ctrl+Alt+F", MOD_CONTROL | MOD_ALT, 0x46),
        ("Ctrl+Shift+Space", MOD_CONTROL | MOD_SHIFT, 0x20),
        ("Win+Shift+F", MOD_WIN | MOD_SHIFT, 0x46),
        ("Ctrl+Alt+Space", MOD_CONTROL | MOD_ALT, 0x20),
    };

    public static GlobalHotkey Instance { get; } = new();

    UserStore? _store;
    IntPtr _hwnd;
    SUBCLASSPROC? _proc;
    Action? _onPressed;
    bool _registered;

    public bool Enabled => _store?.GetSetting("hotkey.search") == "1";
    public int ChoiceIndex => int.TryParse(_store?.GetSetting("hotkey.searchKey"), out int i) && i >= 0 && i < Choices.Length ? i : 0;

    public void Initialize(UserStore store, IntPtr hwnd, Action onPressed)
    {
        _store = store;
        _hwnd = hwnd;
        _onPressed = onPressed;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, (UIntPtr)0xF1B1, UIntPtr.Zero);
        Apply();
    }

    /// <summary>設定を変えて登録し直す。登録できなかったら false（ほかのアプリが同じキーを使っている）。</summary>
    public bool Set(bool enabled, int choice)
    {
        _store?.SetSetting("hotkey.search", enabled ? "1" : "0");
        _store?.SetSetting("hotkey.searchKey", choice.ToString());
        return Apply();
    }

    bool Apply()
    {
        if (_registered) { UnregisterHotKey(_hwnd, HotkeyId); _registered = false; }
        if (!Enabled || _hwnd == IntPtr.Zero) return true;
        var (_, mods, key) = Choices[ChoiceIndex];
        _registered = RegisterHotKey(_hwnd, HotkeyId, mods | MOD_NOREPEAT, key);
        if (!_registered) AppLog.Info($"ホットキー {Choices[ChoiceIndex].Label} を登録できませんでした（ほかのアプリが使用中）");
        return _registered;
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_HOTKEY && wParam == (IntPtr)HotkeyId)
        {
            _onPressed?.Invoke();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
        if (_proc != null && _hwnd != IntPtr.Zero) RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)0xF1B1);
    }

    delegate IntPtr SUBCLASSPROC(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll")]
    static extern bool SetWindowSubclass(IntPtr hwnd, SUBCLASSPROC proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    static extern bool RemoveWindowSubclass(IntPtr hwnd, SUBCLASSPROC proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
