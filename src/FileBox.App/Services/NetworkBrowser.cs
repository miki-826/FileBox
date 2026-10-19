using System.Runtime.InteropServices;

namespace FileBox.App.Services;

/// <summary>ネットワーク上の場所（コンピューター・共有フォルダ・ネットワークドライブ）。</summary>
public sealed record NetworkPlace(string Name, string Path, NetworkPlaceKind Kind);

public enum NetworkPlaceKind { Computer, Share, Drive }

/// <summary>
/// ネットワークを FileBox の中で見る（エクスプローラーを開かない）。コンピューターの一覧は Windows の「ネットワーク」と同じ
/// （ネットワーク探索が有効なときだけ見える）。共有フォルダを開くと \\サーバー\共有 を通常のフォルダとして表示する。
/// </summary>
public static class NetworkBrowser
{
    /// <summary>ネットワーク上のコンピューター。Shell の COM は STA が必要なので専用スレッドで読む（探索に数秒かかることがある）。</summary>
    public static Task<List<NetworkPlace>> ComputersAsync() => RunSta(() =>
    {
        var result = new List<NetworkPlace>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return result;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic? network = shell.NameSpace(18);   // ssfNETWORK
            if (network == null) return result;
            foreach (dynamic item in network.Items())
            {
                string path = item.Path ?? "";
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) result.Add(new NetworkPlace((string)item.Name, path, NetworkPlaceKind.Computer));
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>割り当て済みのネットワークドライブ。</summary>
    public static List<NetworkPlace> MappedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network)
                 .Select(d => new NetworkPlace($"{(d.IsReady ? d.VolumeLabel : "")} ({d.Name.TrimEnd('\\')})".Trim(), d.RootDirectory.FullName, NetworkPlaceKind.Drive))
                 .ToList();

    /// <summary>\\サーバー の共有フォルダ（管理用の C$ などは除く）。</summary>
    public static Task<List<NetworkPlace>> SharesAsync(string server) => Task.Run(() =>
    {
        var result = new List<NetworkPlace>();
        string host = server.TrimStart('\\').TrimEnd('\\');
        int status = NetShareEnum(@"\\" + host, 1, out var buffer, MAX_PREFERRED_LENGTH, out int read, out _, IntPtr.Zero);
        try
        {
            if (status != 0) throw new IOException(Core.Files.FriendlyError.FromWin32(status).ToString());
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                if ((info.shi1_type & 0xFF) != STYPE_DISKTREE || (info.shi1_type & STYPE_SPECIAL) != 0 || info.shi1_netname.EndsWith('$')) continue;
                result.Add(new NetworkPlace(info.shi1_netname, $@"\\{host}\{info.shi1_netname}", NetworkPlaceKind.Share));
            }
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    const int MAX_PREFERRED_LENGTH = -1;
    const uint STYPE_DISKTREE = 0;
    const uint STYPE_SPECIAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);
}
