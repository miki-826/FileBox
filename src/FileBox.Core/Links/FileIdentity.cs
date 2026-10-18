using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileBox.Core.Links;

/// <summary>ボリュームシリアル＋128bit ファイルID（FILE_ID_INFO）。名前変更・同一ボリューム内の移動では変わらない。</summary>
public readonly record struct FileId(long VolumeSerial, byte[] Id);

/// <summary>
/// ブックマーク追跡用（02_機能設計 §4.3）。パス→ファイルID と、ファイルID→現在のパス（OpenFileById）を求める。
/// どちらも管理者権限は不要。
/// </summary>
public static class FileIdentity
{
    public static FileId? TryGet(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return null;
        return Query(handle);
    }

    /// <summary>ファイルIDから現在のパスを求める。見つからなければ null（削除・別ボリュームへ移動）。</summary>
    public static string? TryResolve(FileId id)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            using var root = Open(drive.RootDirectory.FullName);
            if (root.IsInvalid || Query(root) is not { } rootId || rootId.VolumeSerial != id.VolumeSerial) continue;

            var descriptor = new FILE_ID_DESCRIPTOR { dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = ExtendedFileIdType, ExtendedFileId = id.Id };
            using var file = OpenFileById(root, ref descriptor, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
            if (file.IsInvalid) return null;
            var buffer = new char[1024];
            uint len = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len >= buffer.Length) return null;
            string path = new(buffer, 0, (int)len);
            if (path.StartsWith(@"\\?\UNC\")) return @"\\" + path[8..];
            return path.StartsWith(@"\\?\") ? path[4..] : path;
        }
        return null;
    }

    static SafeFileHandle Open(string path) =>
        CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    static FileId? Query(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO info, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
            return null;
        return new FileId((long)info.VolumeSerialNumber, info.FileId);
    }

    // ---------------------------------------------------------------- Win32

    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;   // フォルダも開ける
    const int FileIdInfo = 18;
    const int ExtendedFileIdType = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ExtendedFileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FILE_ID_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FILE_ID_DESCRIPTOR id, uint access, FileShare share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);
}
