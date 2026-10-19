using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace FileBox.App.Services;

/// <summary>
/// Windows のプレビュー ハンドラー（エクスプローラーのプレビュー ウィンドウと同じ仕組み）で、Office などを表示する。
/// 子ウィンドウを XAML の領域の上に重ねて置くので、メニューやダイアログが開いている間は隠す。
/// ハンドラーが入っていない種類は使えない（その場合は呼び出し側が「未対応」を表示する）。
/// </summary>
public sealed class PreviewHandlerHost : IDisposable
{
    static readonly string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    readonly FrameworkElement _area;
    IntPtr _child;
    object? _handler;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    bool _hiddenForPopup;

    public PreviewHandlerHost(FrameworkElement area) => _area = area;

    /// <summary>この拡張子のプレビュー ハンドラー（CLSID）。無ければ null。</summary>
    public static Guid? HandlerFor(string ext)
    {
        try
        {
            string? clsid = Registry.ClassesRoot.OpenSubKey($@"{ext}\ShellEx\{PreviewHandlerGuid}")?.GetValue("") as string;
            if (clsid == null && Registry.ClassesRoot.OpenSubKey(ext)?.GetValue("") is string progId)
                clsid = Registry.ClassesRoot.OpenSubKey($@"{progId}\ShellEx\{PreviewHandlerGuid}")?.GetValue("") as string;
            return Guid.TryParse(clsid, out var g) ? g : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>path を表示する。表示できなければ false。</summary>
    public bool Show(string path, IntPtr window)
    {
        Unload();
        if (HandlerFor(Path.GetExtension(path)) is not Guid clsid) return false;
        try
        {
            _child = CreateWindowEx(0, "STATIC", null, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0, 0, 1, 1, window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            _handler = Activator.CreateInstance(type);
            switch (_handler)
            {
                case IInitializeWithFile file:
                    file.Initialize(path, 0);
                    break;
                case IInitializeWithStream stream:
                    if (SHCreateStreamOnFileEx(path, 0 /* STGM_READ */, 0, false, IntPtr.Zero, out var istream) != 0) throw new IOException("ファイルを開けませんでした");
                    stream.Initialize(istream, 0);
                    break;
                default:
                    throw new NotSupportedException("このプレビュー ハンドラーには対応していません");
            }
            var rect = Bounds();
            ((IPreviewHandler)_handler).SetWindow(_child, ref rect);
            Place();
            ((IPreviewHandler)_handler).DoPreview();
            _area.SizeChanged += OnLayout;
            _area.LayoutUpdated += OnLayout;
            _timer ??= CreateTimer();
            _timer.Start();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"プレビュー ハンドラーで表示できませんでした: {path}", ex);
            Unload();
            return false;
        }
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTimer()
    {
        var t = _area.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(250);
        t.Tick += (_, _) => CheckPopups();
        return t;
    }

    /// <summary>メニュー・ダイアログ・ツールチップなどのポップアップが開いている間は隠す（子ウィンドウはそれらより手前に描かれるため）。</summary>
    void CheckPopups()
    {
        if (_child == IntPtr.Zero || _area.XamlRoot == null) return;
        bool popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(_area.XamlRoot).Any(p => p.Child is not Microsoft.UI.Xaml.Controls.ToolTip);
        bool visible = _area.IsLoaded && _area.Visibility == Visibility.Visible && _area.ActualWidth > 0;
        bool hide = popups || !visible;
        if (hide == _hiddenForPopup) return;
        _hiddenForPopup = hide;
        ShowWindow(_child, hide ? SW_HIDE : SW_SHOW);
    }

    void OnLayout(object? sender, object e) => Place();

    RECT Bounds()
    {
        double scale = _area.XamlRoot?.RasterizationScale ?? 1;
        var p = _area.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new RECT
        {
            Left = (int)(p.X * scale),
            Top = (int)(p.Y * scale),
            Right = (int)((p.X + _area.ActualWidth) * scale),
            Bottom = (int)((p.Y + _area.ActualHeight) * scale),
        };
    }

    RECT _last;

    void Place()
    {
        if (_child == IntPtr.Zero || _area.XamlRoot == null) return;
        var r = Bounds();
        if (r.Left == _last.Left && r.Top == _last.Top && r.Right == _last.Right && r.Bottom == _last.Bottom) return;
        _last = r;
        SetWindowPos(_child, HWND_TOP, r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top), SWP_NOACTIVATE);
        var inner = new RECT { Left = 0, Top = 0, Right = r.Right - r.Left, Bottom = r.Bottom - r.Top };
        try { (_handler as IPreviewHandler)?.SetRect(ref inner); } catch (Exception) { }
    }

    public void Unload()
    {
        _timer?.Stop();
        _area.SizeChanged -= OnLayout;
        _area.LayoutUpdated -= OnLayout;
        if (_handler != null)
        {
            try { ((IPreviewHandler)_handler).Unload(); } catch (Exception) { }
            try { Marshal.FinalReleaseComObject(_handler); } catch (Exception) { }
            _handler = null;
        }
        if (_child != IntPtr.Zero) DestroyWindow(_child);
        _child = IntPtr.Zero;
        _last = default;
        _hiddenForPopup = false;
    }

    public void Dispose() => Unload();

    // ---------------------------------------------------------------- Win32 / COM

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    const int SW_HIDE = 0, SW_SHOW = 5;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(IntPtr pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream
    {
        void Initialize(System.Runtime.InteropServices.ComTypes.IStream pstream, uint grfMode);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IntPtr pstmTemplate, out System.Runtime.InteropServices.ComTypes.IStream ppstm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
