using System.IO.Compression;
using System.Security.Cryptography;
using FileBox.Setup;
using Xunit;

namespace FileBox.Tests;

public sealed class SetupPackageTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "FileBox-Setup-Test-" + Guid.NewGuid().ToString("N"));
    readonly string[] _required = ["FileBox.exe", "FileBox.dll", "hostfxr.dll", "coreclr.dll", "indexer/FileBox.Indexer.exe", "indexer/hostfxr.dll", "indexer/coreclr.dll"];

    public SetupPackageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    string CreateZip(params string[] names)
    {
        string zip = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (string name in names)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("確認用データ");
        }
        return zip;
    }

    [Fact]
    public void Verify_AcceptsMatchingChecksum()
    {
        string zip = CreateZip(_required);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        Package.Verify(zip, hash + "  FileBox-preview-win-x64.zip\r\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void Verify_RejectsMalformedOrMismatchedChecksum(string checksum) =>
        Assert.Throws<InvalidDataException>(() => Package.Verify(CreateZip(_required), checksum));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("FileBox.exe:stream")]
    public void Extract_RejectsUnsafePathsBeforeWriting(string name)
    {
        string zip = CreateZip("innocent.txt", name);
        string target = Path.Combine(_root, "app");
        Assert.Throws<InvalidDataException>(() => Package.Extract(zip, target, CancellationToken.None, _ => { }));
        Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void Extract_RequiresBundledRuntimesAndIndexer() =>
        Assert.Throws<InvalidDataException>(() => Package.Extract(CreateZip("FileBox.exe"), Path.Combine(_root, "app"), CancellationToken.None, _ => { }));

    [Fact]
    public void Extract_ReportsProgressAndPreservesFiles()
    {
        int progress = 0;
        string target = Path.Combine(_root, "app");
        Package.Extract(CreateZip(_required), target, CancellationToken.None, value => progress = value);
        Assert.Equal(100, progress);
        Assert.All(_required, file => Assert.Equal("確認用データ", File.ReadAllText(Path.Combine(target, file))));
    }

    [Fact]
    public void Extract_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Package.Extract(CreateZip(_required), Path.Combine(_root, "app"), cancellation.Token, _ => { }));
    }
}
