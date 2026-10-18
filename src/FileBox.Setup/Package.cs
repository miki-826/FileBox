using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace FileBox.Setup
{
    // ダウンローダーは Windows 標準の .NET Framework で動くようにする。
    public static class Package
    {
        public static void Verify(string zipPath, string checksum)
        {
            string[] parts = checksum.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string expected = parts.Length > 0 ? parts[0] : "";
            if (expected.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(expected, "\\A[0-9a-fA-F]{64}\\z"))
                throw new InvalidDataException("配布ファイルの確認情報を読み取れません。時間をおいて再試行してください。");
            using (var hash = SHA256.Create())
            using (var input = File.OpenRead(zipPath))
            {
                string actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ダウンロードしたファイルの確認に失敗しました。更新中の可能性があります。再試行してください。");
            }
        }

        public static void Extract(string zipPath, string destination, CancellationToken cancellation, Action<int> progress)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                long total = 0;
                // 展開前に全パスを検査し、保存先の外への書き込みを拒否する。
                foreach (var entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.IndexOf(':') >= 0 || Path.IsPathRooted(entry.FullName))
                        throw new InvalidDataException("配布ファイルに不正な保存先が含まれています。");
                    total = checked(total + entry.Length);
                    if (total > 2L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("配布ファイルの展開サイズが大きすぎます。");
                }
                long written = 0;
                byte[] buffer = new byte[81920];
                foreach (var entry in archive.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                            written += count;
                            progress(total == 0 ? 100 : (int)(written * 100 / total));
                        }
                    }
                }
            }
            foreach (string file in new[] { "FileBox.exe", "FileBox.dll", "hostfxr.dll", "coreclr.dll", "indexer/FileBox.Indexer.exe", "indexer/hostfxr.dll", "indexer/coreclr.dll" })
                if (!File.Exists(Path.Combine(root, file)))
                    throw new InvalidDataException("配布ファイルに必要なファイルがありません: " + file);
        }
    }
}
