using System.Text;
using FileBox.Core.Files;
using Xunit;

namespace FileBox.Tests;

public class RecycleBinInfoTests
{
    static readonly DateTime Deleted = new(2026, 10, 14, 14, 19, 42, DateTimeKind.Local);

    static byte[] Header(long version, long size)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(version));
        bytes.AddRange(BitConverter.GetBytes(size));
        bytes.AddRange(BitConverter.GetBytes(Deleted.ToFileTime()));
        return bytes.ToArray();
    }

    [Fact]
    public void ParsesVersion2()
    {
        string path = @"C:\Users\me\Downloads\請求書 (1).pdf";
        var data = Header(2, 1364)
            .Concat(BitConverter.GetBytes(path.Length + 1))
            .Concat(Encoding.Unicode.GetBytes(path + "\0"))
            .ToArray();
        var entry = RecycleBinInfo.Parse(data);
        Assert.NotNull(entry);
        Assert.Equal(path, entry!.OriginalPath);
        Assert.Equal(1364, entry.Size);
        Assert.Equal(Deleted, entry.DeletedAt);
    }

    [Fact]
    public void ParsesVersion1FixedLength()
    {
        string path = @"D:\書類\メモ.txt";
        var name = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(name, 0);
        var entry = RecycleBinInfo.Parse(Header(1, 10).Concat(name).ToArray());
        Assert.Equal(path, entry!.OriginalPath);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public void RejectsBrokenData(long version)
    {
        // 版が未知、または長さがデータを超える
        var data = Header(version, 1).Concat(BitConverter.GetBytes(1000)).Concat(new byte[10]).ToArray();
        Assert.Null(RecycleBinInfo.Parse(data));
        Assert.Null(RecycleBinInfo.Parse(new byte[5]));
    }

    [Fact]
    public void MapsInfoNameToDataName() => Assert.Equal("$RAB12CD.txt", RecycleBinInfo.DataName("$IAB12CD.txt"));
}
