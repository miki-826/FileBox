using System.IO.Compression;

namespace FileBox.Indexer.Index;

/// <summary>NTFS ボリュームのインデックス永続化（03_データ設計 §1）。USN 位置と共に保存し、再起動時は差分だけ適用する。</summary>
internal static class Snapshot
{
    const uint Magic = 0x31584246; // "FBX1"
    const int Version = 1;

    public sealed record Header(uint VolumeSerial, ulong JournalId, long NextUsn, ulong RootFrn);

    public static string PathFor(uint volumeSerial)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileBox", "index");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{volumeSerial:X8}.snap");
    }

    public static void Save(string path, Header header, List<RawEntry> entries)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var z = new BrotliStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(z))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(header.VolumeSerial);
            w.Write(header.JournalId);
            w.Write(header.NextUsn);
            w.Write(header.RootFrn);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Frn);
                w.Write(e.ParentFrn);
                w.Write((byte)e.Flags);
                w.Write(e.Size);
                w.Write(e.MTimeUtcTicks);
                w.Write(e.Name);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static (Header Header, List<RawEntry> Entries)? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var z = new BrotliStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(z);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return null;
            var header = new Header(r.ReadUInt32(), r.ReadUInt64(), r.ReadInt64(), r.ReadUInt64());
            int count = r.ReadInt32();
            var list = new List<RawEntry>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new RawEntry
                {
                    Frn = r.ReadUInt64(),
                    ParentFrn = r.ReadUInt64(),
                    Flags = (EntryFlags)r.ReadByte(),
                    Size = r.ReadInt64(),
                    MTimeUtcTicks = r.ReadInt64(),
                    Name = r.ReadString(),
                });
            }
            return (header, list);
        }
        catch (Exception ex)
        {
            Log.Error($"スナップショット読込失敗 {path}", ex);
            return null;
        }
    }
}
