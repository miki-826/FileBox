using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileBox.Indexer.Volumes;

/// <summary>NTFS の MFT 列挙・USN ジャーナル読み取り用 Win32 API。</summary>
internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_READ_ATTRIBUTES = 0x80;

    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    public const uint USN_REASON_DATA_OVERWRITE = 0x1;
    public const uint USN_REASON_DATA_EXTEND = 0x2;
    public const uint USN_REASON_DATA_TRUNCATION = 0x4;
    public const uint USN_REASON_FILE_CREATE = 0x100;
    public const uint USN_REASON_FILE_DELETE = 0x200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x1000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x2000;
    public const uint USN_REASON_BASIC_INFO_CHANGE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>USN_RECORD_V2 のフィールドオフセット。</summary>
    public static class UsnRecord
    {
        public const int RecordLength = 0;
        public const int MajorVersion = 4;
        public const int FileReferenceNumber = 8;
        public const int ParentFileReferenceNumber = 16;
        public const int Usn = 24;
        public const int Reason = 40;
        public const int FileAttributes = 52;
        public const int FileNameLength = 56;
        public const int FileNameOffset = 58;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
