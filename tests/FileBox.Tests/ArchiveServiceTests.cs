using System.IO.Compression;
using FileBox.Core.Files;
using Xunit;

namespace FileBox.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "filebox-zip-" + Guid.NewGuid().ToString("N"));

    public ArchiveServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RoundTripsFoldersWithJapaneseNames()
    {
        string src = Path.Combine(_root, "請求書");
        Directory.CreateDirectory(Path.Combine(src, "2026年", "空フォルダ"));
        File.WriteAllText(Path.Combine(src, "2026年", "山田商事.txt"), "内容");
        File.WriteAllText(Path.Combine(_root, "メモ.txt"), "memo");

        string zip = Path.Combine(_root, "out.zip");
        await ArchiveService.CompressAsync(new[] { src, Path.Combine(_root, "メモ.txt") }, zip);
        string dest = Path.Combine(_root, "展開先");
        await ArchiveService.ExtractAsync(zip, dest);

        Assert.Equal("内容", File.ReadAllText(Path.Combine(dest, "請求書", "2026年", "山田商事.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "メモ.txt")));
    }

    [Fact]
    public async Task ReadsShiftJisNames()
    {
        // Windows 標準の「圧縮フォルダー」で作った日本語名 ZIP 相当（UTF-8 フラグなし・Shift_JIS）
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string zip = Path.Combine(_root, "sjis.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create, System.Text.Encoding.GetEncoding(932)))
        using (var w = new StreamWriter(archive.CreateEntry("見積書/山田商事.txt").Open())) w.Write("x");
        string dest = Path.Combine(_root, "dest-sjis");
        await ArchiveService.ExtractAsync(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "見積書", "山田商事.txt")));
    }

    [Fact]
    public async Task IgnoresEntriesThatEscapeTheDestination()
    {
        string zip = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("../escaped.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(archive.CreateEntry("ok.txt").Open())) w.Write("ok");
        }
        string dest = Path.Combine(_root, "dest");
        await ArchiveService.ExtractAsync(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
