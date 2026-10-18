using FileBox.Core.Links;
using Xunit;

namespace FileBox.Tests;

public class FileBoxUriTests
{
    [Fact]
    public void RoundTripsPathWithJapaneseAndSpaces()
    {
        string link = FileBoxUri.ForPath(@"D:\書類\請求書 2026", select: "山田商事 #1.pdf");
        var parsed = FileBoxUri.Parse(link, out var error);
        Assert.Null(error);
        Assert.Equal(LinkKind.Open, parsed!.Kind);
        Assert.Equal(@"D:\書類\請求書 2026", parsed.Path);
        Assert.Equal("山田商事 #1.pdf", parsed.Select);
    }

    [Theory]
    [InlineData("filebox://bookmark/abcDEF12_-x", LinkKind.Bookmark)]
    [InlineData("filebox://workspace/3", LinkKind.Workspace)]
    [InlineData("filebox://search?q=%E8%AB%8B%E6%B1%82%E6%9B%B8%20ext%3Apdf", LinkKind.Search)]
    [InlineData("FILEBOX://open?path=C%3A%5CUsers", LinkKind.Open)]
    public void ParsesKinds(string link, LinkKind kind)
    {
        Assert.Equal(kind, FileBoxUri.Parse(link, out _)!.Kind);
    }

    [Fact]
    public void SearchQueryIsDecoded()
    {
        Assert.Equal("請求書 ext:pdf", FileBoxUri.Parse(FileBoxUri.ForSearch("請求書 ext:pdf"), out _)!.Query);
    }

    [Theory]
    [InlineData(@"\\server\share\x")]          // UNC（既定で不可）
    [InlineData(@"\\?\C:\Windows")]            // デバイスパス
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\Users\..\Windows")]       // 親参照
    [InlineData(@"relative\path")]             // 相対パス
    [InlineData(@"C:\file.txt:secret")]        // 代替データストリーム
    [InlineData(@"C:")]                        // ドライブ相対
    public void RejectsUnsafePaths(string path)
    {
        var parsed = FileBoxUri.Parse("filebox://open?path=" + Uri.EscapeDataString(path), out var error);
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void AllowsUncWhenEnabled()
    {
        var parsed = FileBoxUri.Parse("filebox://open?path=" + Uri.EscapeDataString(@"\\nas\共有\書類"), out var error, allowUnc: true);
        Assert.True(parsed != null, error);
        Assert.Equal(@"\\nas\共有\書類", parsed!.Path);
    }

    [Theory]
    [InlineData("filebox://open?path=C%3A%5CUsers&select=..%5Cevil.exe")]
    [InlineData("filebox://bookmark/short")]
    [InlineData("filebox://workspace/-1")]
    [InlineData("filebox://run?cmd=calc")]
    [InlineData("https://example.com/open?path=C%3A%5C")]
    public void RejectsMalformedLinks(string link)
    {
        Assert.Null(FileBoxUri.Parse(link, out _));
    }

    [Theory]
    [InlineData(@"C:\a\setup.exe", true)]
    [InlineData(@"C:\a\run.PS1", true)]
    [InlineData(@"C:\a\shortcut.lnk", true)]
    [InlineData(@"C:\a\report.pdf", false)]
    public void DetectsExecutables(string path, bool expected) => Assert.Equal(expected, FileBoxUri.IsExecutable(path));

    [Fact]
    public void EscapesMarkdownLabel() =>
        Assert.Equal(@"[請求書\[2026\]](filebox://x)", FileBoxUri.Markdown("請求書[2026]", "filebox://x"));
}

public class FileIdentityTests
{
    [Fact]
    public void FollowsFolderAfterRenameAndMove()
    {
        string root = Path.Combine(Path.GetTempPath(), "filebox-id-" + Guid.NewGuid().ToString("N"));
        string original = Path.Combine(root, "請求書");
        Directory.CreateDirectory(original);
        try
        {
            var id = FileIdentity.TryGet(original);
            Assert.NotNull(id);
            string moved = Path.Combine(root, "archive", "請求書_2026");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            Directory.Move(original, moved);
            Assert.Equal(moved, FileIdentity.TryResolve(id!.Value), ignoreCase: true);

            Directory.Delete(moved);
            Assert.Null(FileIdentity.TryResolve(id.Value));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
