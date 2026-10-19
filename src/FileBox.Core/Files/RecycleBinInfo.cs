using System.Text;

namespace FileBox.Core.Files;

/// <summary>ごみ箱の1項目の情報（$I ファイルの中身）。</summary>
public sealed record RecycledEntry(string OriginalPath, long Size, DateTime DeletedAt);

/// <summary>
/// ごみ箱（ドライブ\$Recycle.Bin\SID）の $I ファイルを読む。実体は同じ名前の $R ファイル（またはフォルダ）。
/// 形式: Windows Vista〜8.1 は版 1（パス 260 文字固定）、Windows 10 以降は版 2（パスの長さ付き）。
/// </summary>
public static class RecycleBinInfo
{
    const int HeaderSize = 24;   // 版(8) + サイズ(8) + 削除日時 FILETIME(8)
    const int V1PathBytes = 520;

    public static RecycledEntry? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) return null;
        long version = BitConverter.ToInt64(data[..8]);
        long size = BitConverter.ToInt64(data[8..16]);
        long fileTime = BitConverter.ToInt64(data[16..24]);
        string path;
        if (version == 1)
        {
            if (data.Length < HeaderSize + V1PathBytes) return null;
            path = Encoding.Unicode.GetString(data.Slice(HeaderSize, V1PathBytes));
        }
        else if (version == 2)
        {
            if (data.Length < HeaderSize + 4) return null;
            int chars = BitConverter.ToInt32(data.Slice(HeaderSize, 4));
            if (chars <= 0 || HeaderSize + 4 + (long)chars * 2 > data.Length) return null;
            path = Encoding.Unicode.GetString(data.Slice(HeaderSize + 4, chars * 2));
        }
        else return null;

        int nul = path.IndexOf('\0');
        if (nul >= 0) path = path[..nul];
        if (path.Length == 0) return null;
        DateTime deleted;
        try { deleted = fileTime > 0 ? DateTime.FromFileTime(fileTime) : DateTime.MinValue; }
        catch (ArgumentOutOfRangeException) { deleted = DateTime.MinValue; }
        return new RecycledEntry(path, Math.Max(0, size), deleted);
    }

    /// <summary>$I ファイル名から実体（$R）の名前を作る（$IAB12CD.txt → $RAB12CD.txt）。</summary>
    public static string DataName(string infoName) =>
        infoName.StartsWith("$I", StringComparison.OrdinalIgnoreCase) ? "$R" + infoName[2..] : infoName;
}
