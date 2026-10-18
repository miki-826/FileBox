using System.Runtime.InteropServices;
using System.Text;
using FileBox.Core.Suggest;
using FileBox.Data;
using Microsoft.UI.Dispatching;

namespace FileBox.App.Services;

/// <summary>
/// 他アプリの「名前を付けて保存」への提案（02_機能設計 §1.1 入口③・§1.2）。
/// 標準のファイルダイアログ（#32770）が開いたらファイル名欄を監視し、入力に合わせて保存先候補を横に表示する。
/// 候補を選ぶとファイル名欄に「フォルダ\ファイル名」を入れる。保存されたことを確かめてから学習する。
/// UI Automation の代わりに WinEvent フックとウィンドウメッセージを使う（標準ダイアログならこれで足り、軽い）。
/// </summary>
public sealed class SaveDialogWatcher : IDisposable
{
    static SaveDialogWatcher? _instance;
    public static SaveDialogWatcher Instance => _instance ?? throw new InvalidOperationException("SaveDialogWatcher.Initialize が未実行");

    const int FileNameEditId = 1001;     // Vista 以降: ファイル名の Edit
    const int FileNameComboId = 0x47C;   // 旧形式 cmb13: ファイル名
    readonly UserStore _store;
    readonly DispatcherQueue _ui;
    readonly WinEventDelegate _proc;   // GC されないよう保持
    IntPtr _hook;
    DispatcherQueueTimer? _timer;

    IntPtr _dialog, _edit;
    string _lastText = "";
    DateTime _lastChange;
    bool _suggestedFor;
    CancellationTokenSource? _cts;
    Views.SaveSuggestPanel? _panel;
    (string FileName, string Folder, SuggestionResult? Shown)? _pendingLearn;

    SaveDialogWatcher(UserStore store, DispatcherQueue ui)
    {
        _store = store;
        _ui = ui;
        _proc = OnWinEvent;
    }

    public static void Initialize(UserStore store, DispatcherQueue ui)
    {
        if (_instance != null) return;
        _instance = new SaveDialogWatcher(store, ui);
        if (_instance.Enabled) _instance.Start();
    }

    public bool Enabled => _store.GetSetting("saveDialog.suggest") != "0";

    public void SetEnabled(bool on)
    {
        _store.SetSetting("saveDialog.suggest", on ? "1" : "0");
        if (on) Start(); else Stop();
    }

    void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Poll();
    }

    void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _timer?.Stop();
        EndTracking();
    }

    // ================================================================ ダイアログの検出

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || hwnd == _dialog) return;
        if (ClassName(hwnd) != "#32770" || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        IntPtr edit = FindFileNameEdit(hwnd);
        if (edit == IntPtr.Zero || !IsSaveDialog(hwnd)) return;
        EndTracking();
        _dialog = hwnd;
        _edit = edit;
        _lastText = "";
        _suggestedFor = false;
        _timer?.Start();
    }

    /// <summary>
    /// ファイル名欄の Edit。Vista 以降のダイアログは ComboBox 内の Edit（ID 1001）、
    /// 旧形式は cmb13（0x47C）配下。アドレスバーも ComboBoxEx32 の Edit なので ID で区別する。
    /// </summary>
    static IntPtr FindFileNameEdit(IntPtr dialog)
    {
        IntPtr modern = IntPtr.Zero, legacy = IntPtr.Zero;
        EnumChildWindows(dialog, (child, _) =>
        {
            int id = GetDlgCtrlID(child);
            if (id == FileNameEditId && ClassName(child) == "Edit" && ClassName(GetParent(child)) == "ComboBox")
            {
                modern = child;
                return false;
            }
            if (id == FileNameComboId && legacy == IntPtr.Zero) legacy = FindEditBelow(child);
            return true;
        }, IntPtr.Zero);
        return modern != IntPtr.Zero ? modern : legacy;
    }

    static IntPtr FindEditBelow(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        if (ClassName(parent) == "Edit") return parent;
        EnumChildWindows(parent, (child, _) =>
        {
            if (ClassName(child) == "Edit") { found = child; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>OK ボタン（ID 1）の文字が「保存」なら保存ダイアログ。</summary>
    static bool IsSaveDialog(IntPtr dialog)
    {
        IntPtr ok = GetDlgItem(dialog, 1);
        string text = ok != IntPtr.Zero ? GetText(ok) : "";
        return text.Contains("保存") || text.Contains("Save", StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ 入力の監視と提案

    void Poll()
    {
        if (_dialog == IntPtr.Zero) { _timer?.Stop(); return; }
        if (!IsWindow(_dialog) || !IsWindowVisible(_dialog))
        {
            OnDialogClosed();
            return;
        }
        string text = GetText(_edit).Trim().Trim('"');
        if (text != _lastText)
        {
            _lastText = text;
            _lastChange = DateTime.Now;
            _suggestedFor = false;
        }
        _panel?.FollowDialog(_dialog);
        // 入力が落ち着いてから（150ms）提案を出し直す。パスを入れた後（\ を含む）は出さない
        if (!_suggestedFor && DateTime.Now - _lastChange > TimeSpan.FromMilliseconds(150) && text.Length > 0 && !text.Contains('\\'))
        {
            _suggestedFor = true;
            _ = SuggestAsync(text);
        }
    }

    async Task SuggestAsync(string fileName)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var result = await SuggestionService.Instance.SuggestAsync(fileName, cts.Token);
            if (cts.IsCancellationRequested || _dialog == IntPtr.Zero) return;
            _panel ??= new Views.SaveSuggestPanel(Choose, Hide);
            _panel.Show(_dialog, fileName, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    /// <summary>候補を選んだ: 必要ならフォルダを作り、ファイル名欄にフルパスを入れてダイアログへ戻す。</summary>
    void Choose(string fileName, string folder, bool isNew, SuggestionResult? shown)
    {
        if (_dialog == IntPtr.Zero || !IsWindow(_dialog)) return;
        try
        {
            if (isNew)
            {
                if (!Guard.Can(Core.Security.Permission.Create, Path.GetDirectoryName(folder) ?? folder, "save")) return;
                Directory.CreateDirectory(folder);
                Guard.Created("save", folder);
            }
            else if (!Guard.Can(Core.Security.Permission.Create, folder, "save")) return;
        }
        catch (Exception ex) { App.ShowError("フォルダを作れませんでした", ex); return; }
        string full = Path.Combine(folder, fileName);
        SendMessage(_edit, WM_SETTEXT, IntPtr.Zero, full);
        _lastText = full;
        _pendingLearn = (fileName, folder, shown);
        SetForegroundWindow(_dialog);
        SetFocus(_edit);
    }

    void Hide() => _panel?.HidePanel();

    void OnDialogClosed()
    {
        var pending = _pendingLearn;
        EndTracking();
        if (pending is not { } p) return;
        // 実際に保存されたら（少し待ってファイルができていれば）学習する。キャンセルなら何もしない
        _ = Task.Run(async () =>
        {
            string path = Path.Combine(p.Folder, p.FileName);
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(path) || Directory.EnumerateFiles(p.Folder, Path.GetFileNameWithoutExtension(p.FileName) + ".*").Any())
                {
                    _ui.TryEnqueue(() =>
                    {
                        SuggestionService.Instance.Record(p.FileName, p.Folder, p.Shown);
                        Guard.Created("save", path);
                    });
                    return;
                }
                await Task.Delay(500);
            }
        });
    }

    void EndTracking()
    {
        _cts?.Cancel();
        _dialog = IntPtr.Zero;
        _edit = IntPtr.Zero;
        _pendingLearn = null;
        _timer?.Stop();
        _panel?.HidePanel();
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- Win32

    static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetText(IntPtr hwnd)
    {
        int len = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        SendMessage(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
        return sb.ToString();
    }

    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint GA_ROOT = 2;
    const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;

    delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetDlgCtrlID(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hwnd);
}
