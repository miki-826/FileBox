using System.Runtime.InteropServices;
using Windows.Security.Credentials.UI;

namespace FileBox.App.Services;

/// <summary>
/// 管理操作の前の本人確認（02_機能設計 §6.1）。Windows Hello、使えなければ Windows の資格情報ダイアログ。
/// パスワードは FileBox に保存しない。確認後 5 分間は再確認しない。
/// </summary>
public static class AdminVerifier
{
    static DateTime _verifiedAt = DateTime.MinValue;
    static readonly TimeSpan Valid = TimeSpan.FromMinutes(5);

    public static async Task<bool> VerifyAsync(string reason)
    {
        if (DateTime.Now - _verifiedAt < Valid) return true;
        var hwnd = App.Window != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.Window) : IntPtr.Zero;
        bool ok;
        string method;
        var availability = await UserConsentVerifier.CheckAvailabilityAsync();
        if (availability == UserConsentVerifierAvailability.Available)
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, $"FileBox: {reason}");
            ok = result == UserConsentVerificationResult.Verified;
            method = "Windows Hello";
        }
        else
        {
            ok = await Task.Run(() => PromptWindowsCredentials(hwnd, reason));
            method = "Windows 資格情報";
        }
        PolicyService.Instance.Audit("admin_verify", null, null, null, ok ? "ok" : "denied", $"{method}: {reason}");
        if (ok) _verifiedAt = DateTime.Now;
        return ok;
    }

    /// <summary>現在のユーザーのパスワードを Windows のダイアログで入力してもらい、LogonUser で確かめる。</summary>
    static bool PromptWindowsCredentials(IntPtr hwnd, string reason)
    {
        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = hwnd,
            pszCaptionText = "FileBox の管理操作",
            pszMessageText = $"{reason}のため、Windows のパスワードを入力してください",
        };
        uint authPackage = 0;
        int rc = CredUIPromptForWindowsCredentials(ref info, 0, ref authPackage, IntPtr.Zero, 0, out var outBuffer, out uint outSize, IntPtr.Zero, CREDUIWIN_ENUMERATE_CURRENT_USER);
        if (rc != 0) return false;
        try
        {
            var user = new System.Text.StringBuilder(256);
            var domain = new System.Text.StringBuilder(256);
            var password = new System.Text.StringBuilder(256);
            int u = user.Capacity, d = domain.Capacity, p = password.Capacity;
            if (!CredUnPackAuthenticationBuffer(0, outBuffer, outSize, user, ref u, domain, ref d, password, ref p)) return false;
            string name = user.ToString();
            string dom = domain.ToString();
            if (dom.Length == 0 && name.Contains('\\')) { dom = name[..name.IndexOf('\\')]; name = name[(name.IndexOf('\\') + 1)..]; }
            // 本人のアカウントであること
            using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
            string expected = current.Name[(current.Name.IndexOf('\\') + 1)..];
            if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase) && !current.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
            bool ok = LogonUser(name, dom.Length > 0 ? dom : ".", password.ToString(), LOGON32_LOGON_INTERACTIVE, 0, out var token);
            password.Clear();
            if (ok) CloseHandle(token);
            return ok;
        }
        finally
        {
            RtlZeroMemory(outBuffer, (UIntPtr)outSize);
            CoTaskMemFree(outBuffer);
        }
    }

    const uint CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;
    const int LOGON32_LOGON_INTERACTIVE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage, IntPtr inBuffer, uint inSize,
        out IntPtr outBuffer, out uint outSize, IntPtr save, uint flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, System.Text.StringBuilder user, ref int userLen,
        System.Text.StringBuilder domain, ref int domainLen, System.Text.StringBuilder password, ref int passwordLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr p);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    static extern void RtlZeroMemory(IntPtr dest, UIntPtr size);
}
