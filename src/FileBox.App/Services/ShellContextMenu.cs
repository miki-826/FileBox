using System.Runtime.InteropServices;

namespace FileBox.App.Services;

/// <summary>
/// Windows 標準の右クリックメニュー（IContextMenu）をそのまま表示する（02_機能設計 §7.2）。
/// 7-Zip・VS Code など他アプリが追加した項目も使える。「送る」「新規作成」などのサブメニューが
/// 描画されるよう、表示中はウィンドウのメッセージを IContextMenu3 に中継する。
/// </summary>
public static class ShellContextMenu
{
    const int FirstCommand = 1;
    const int LastCommand = 0x7FFF;

    static IContextMenu2? _active2;
    static IContextMenu3? _active3;

    /// <summary>paths（同じフォルダ内の項目）のメニューを画面座標 (x, y) に表示し、選ばれた操作を実行する。</summary>
    public static void Show(IntPtr hwnd, IReadOnlyList<string> paths, int x, int y) => Run(hwnd, paths, background: false, x, y);

    /// <summary>フォルダの何もない所を右クリックしたときのメニュー（新規作成・貼り付け・表示など）。</summary>
    public static void ShowBackground(IntPtr hwnd, string folder, int x, int y) => Run(hwnd, new[] { folder }, background: true, x, y);

    static void Run(IntPtr hwnd, IReadOnlyList<string> paths, bool background, int x, int y)
    {
        if (paths.Count == 0) return;
        var pidls = new List<IntPtr>();
        IntPtr menu = IntPtr.Zero;
        object? parentObj = null;
        IContextMenu? cm = null;
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0) return;
                pidls.Add(pidl);
            }
            var iidShellFolder = typeof(IShellFolder).GUID;
            var iidContextMenu = typeof(IContextMenu).GUID;
            if (background)
            {
                // フォルダ自身の IShellFolder からビュー背景用のメニューを作る
                if (SHBindToObject(IntPtr.Zero, pidls[0], IntPtr.Zero, ref iidShellFolder, out parentObj) != 0 || parentObj is not IShellFolder folder) return;
                folder.CreateViewObject(hwnd, ref iidContextMenu, out var ptr);
                cm = (IContextMenu)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
            }
            else
            {
                // 親フォルダの IShellFolder と、各項目の相対 PIDL
                if (SHBindToParent(pidls[0], ref iidShellFolder, out parentObj, out _) != 0 || parentObj is not IShellFolder parent) return;
                var children = pidls.Select(ILFindLastID).ToArray();
                if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidContextMenu, IntPtr.Zero, out var cmObj) != 0) return;
                cm = (IContextMenu)cmObj;
            }

            menu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) flags |= CMF_EXTENDEDVERBS;   // Shift+右クリックの拡張メニュー
            cm.QueryContextMenu(menu, 0, FirstCommand, LastCommand, flags);

            _active2 = cm as IContextMenu2;
            _active3 = cm as IContextMenu3;
            using (new MessageForwarder(hwnd))
            {
                uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
                if (cmd >= FirstCommand)
                {
                    var info = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                        fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                        hwnd = hwnd,
                        lpVerb = (IntPtr)(cmd - FirstCommand),
                        lpVerbW = (IntPtr)(cmd - FirstCommand),
                        nShow = 1,
                        ptInvoke = new POINT { X = x, Y = y },
                    };
                    cm.InvokeCommand(ref info);
                }
            }
        }
        catch (Exception ex) { App.ShowError("メニューを表示できませんでした", ex); }
        finally
        {
            _active2 = null;
            _active3 = null;
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (parentObj != null) Marshal.ReleaseComObject(parentObj);
            foreach (var p in pidls) CoTaskMemFree(p);
        }
    }

    /// <summary>メニュー表示中だけウィンドウをサブクラス化し、描画用メッセージを IContextMenu2/3 へ渡す。</summary>
    sealed class MessageForwarder : IDisposable
    {
        readonly IntPtr _hwnd;
        readonly SUBCLASSPROC _proc;

        public MessageForwarder(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _proc = WndProc;
            SetWindowSubclass(hwnd, _proc, (UIntPtr)0xF11E, UIntPtr.Zero);
        }

        static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
            {
                try
                {
                    if (_active3 != null && _active3.HandleMenuMsg2(msg, wParam, lParam, out var result) == 0) return result;
                    if (_active2 != null && _active2.HandleMenuMsg(msg, wParam, lParam) == 0) return IntPtr.Zero;
                }
                catch (Exception) { }
            }
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose() => RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)0xF11E);
    }

    // ---------------------------------------------------------------- COM / Win32

    const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    const uint WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    const int VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attributes);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr strRet);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    delegate IntPtr SUBCLASSPROC(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr pidlLast);

    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    /// <summary>XAML 要素上の位置（DIP）を画面のピクセル座標に変換する。</summary>
    public static (int X, int Y) ToScreen(Microsoft.UI.Xaml.UIElement element, Windows.Foundation.Point position, IntPtr hwnd)
    {
        var p = element.TransformToVisual(null).TransformPoint(position);
        double scale = element.XamlRoot?.RasterizationScale ?? 1.0;
        var pt = new POINT { X = (int)(p.X * scale), Y = (int)(p.Y * scale) };
        ClientToScreen(hwnd, ref pt);
        return (pt.X, pt.Y);
    }

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr p);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    static extern short GetKeyState(int vk);

    [DllImport("comctl32.dll")]
    static extern bool SetWindowSubclass(IntPtr hwnd, SUBCLASSPROC proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    static extern bool RemoveWindowSubclass(IntPtr hwnd, SUBCLASSPROC proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
