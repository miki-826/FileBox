using System.Runtime.InteropServices;
using FileBox.Data;

namespace FileBox.App.Services;

/// <summary>
/// ネットワークドライブの割り当てと切断（利用者が明示したときだけ行う）。「サインイン時に再接続」は既定でオフ（Windows に記憶させない）。
/// FileBox で割り当てたものは user.db に記録し、設定の「FileBox が変更した Windows の設定」から切断できる。
/// </summary>
public static class NetworkDrives
{
    const string MappedKey = "network.mapped";

    /// <summary>空いているドライブ文字（Z から逆順。エクスプローラーと同じ）。</summary>
    public static IReadOnlyList<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse().Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>割り当てる。成功なら null、失敗なら Win32 エラーコード。</summary>
    public static int? Map(UserStore store, char letter, string share, bool reconnect, string? user = null, string? password = null)
    {
        var resource = new NETRESOURCE { dwType = RESOURCETYPE_DISK, lpLocalName = $"{letter}:", lpRemoteName = share.TrimEnd('\\') };
        int result = WNetAddConnection2(ref resource, password, string.IsNullOrEmpty(user) ? null : user, reconnect ? CONNECT_UPDATE_PROFILE : 0);
        if (result != 0) return result;
        var mapped = Mapped(store);
        mapped[$"{letter}:"] = share;
        store.SetSetting(MappedKey, System.Text.Json.JsonSerializer.Serialize(mapped));
        Guard.Done("network", share, $"{letter}:", reconnect ? "ネットワークドライブの割り当て（再接続あり）" : "ネットワークドライブの割り当て");
        return null;
    }

    /// <summary>切断する。成功なら null、失敗なら Win32 エラーコード。</summary>
    public static int? Disconnect(UserStore store, string drive)
    {
        // 記憶された接続（再接続の設定）も消す
        int result = WNetCancelConnection2(drive.TrimEnd('\\'), CONNECT_UPDATE_PROFILE, false);
        if (result != 0 && result != ERROR_NOT_CONNECTED) return result;
        var mapped = Mapped(store);
        if (mapped.Remove(drive.TrimEnd('\\'))) store.SetSetting(MappedKey, System.Text.Json.JsonSerializer.Serialize(mapped));
        Guard.Done("network", drive, null, "ネットワークドライブの切断");
        return null;
    }

    /// <summary>FileBox で割り当てたドライブ（ドライブ → 共有）。</summary>
    public static Dictionary<string, string> Mapped(UserStore store)
    {
        try
        {
            if (store.GetSetting(MappedKey) is { Length: > 0 } json && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } map)
                return new(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>割り当て済みのネットワークドライブ（FileBox 以外で割り当てたものも含む）。</summary>
    public static IReadOnlyList<(string Drive, string Share)> All() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network).Select(d => (d.Name.TrimEnd('\\'), RemoteName(d.Name.TrimEnd('\\')) ?? "")).ToList();

    static string? RemoteName(string drive)
    {
        var sb = new System.Text.StringBuilder(512);
        int size = sb.Capacity;
        return WNetGetConnection(drive, sb, ref size) == 0 ? sb.ToString() : null;
    }

    const int RESOURCETYPE_DISK = 1;
    const int CONNECT_UPDATE_PROFILE = 1;
    const int ERROR_NOT_CONNECTED = 2250;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);
}
