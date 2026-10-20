using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileBox.App.Services;

/// <summary>
/// ファイルを使っているアプリの名前（Restart Manager）。「使用中のため移動できません」に、どのアプリかを添えるために使う。
/// 調べるだけで、アプリを終了させることはない。
/// </summary>
public static class FileLocks
{
    public static IReadOnlyList<string> WhoIsUsing(string path)
    {
        var names = new List<string>();
        if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) return names;
        try
        {
            var files = Directory.Exists(path)
                ? SafeFiles(path).Take(200).ToArray()
                : new[] { path };
            if (files.Length == 0 || RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return names;
            uint needed = 0, count = 0, reasons = 0;
            int result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ERROR_MORE_DATA || needed == 0) return names;
            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return names;
            for (int i = 0; i < count; i++)
            {
                string name = infos[i].strAppName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = Process.GetProcessById(infos[i].Process.dwProcessId).ProcessName; }
                    catch (ArgumentException) { continue; }
                    catch (InvalidOperationException) { continue; }
                }
                if (!names.Contains(name)) names.Add(name);
            }
        }
        catch (Exception ex) { AppLog.Error("使用中のアプリを調べられませんでした", ex); }
        finally { RmEndSession(session); }
        return names;
    }

    static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
