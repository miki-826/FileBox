using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FileBox.Core.Files;
using FileBox.Core.Settings;
using FileBox.Core.Tools;
using FileBox.Tools;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace FileBox.Tests;

public sealed class ConversionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "FileBox-ConversionTests-" + Guid.NewGuid().ToString("N"));
    public ConversionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    string FileAt(string name, string content = "original") { string path = Path.Combine(_root, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("[1,true,null]", "[\n  1,\n  true,\n  null\n]")]
    public void JsonAcceptsAnyValidRoot(string input, string expected) => Assert.Equal(expected.Replace("\n", Environment.NewLine), DataConversion.Convert(input, ToolKind.JsonFormat, new() { Format = "json" }));

    [Fact]
    public void JsonSortsNestedKeysButPreservesArrayOrder()
    {
        string result = DataConversion.Convert("{\"z\":1,\"a\":[{\"b\":2,\"a\":1},3,2]}", ToolKind.JsonSort, new() { Format = "json" });
        var json = JsonNode.Parse(result)!.AsObject();
        Assert.Equal(new[] { "a", "z" }, json.Select(p => p.Key));
        Assert.Equal(new[] { "a", "b" }, json["a"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(3, (int)json["a"]![1]!); Assert.Equal(2, (int)json["a"]![2]!);
        Assert.Throws<FormatException>(() => DataConversion.ParseJson("{\"a\":1,\"a\":2}"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DataConversion.ParseJson("{broken}"));
    }

    [Fact]
    public void CsvHandlesQuotesNewlinesAndTrailingEmptyCells()
    {
        var rows = DataConversion.ParseCsv("name,value,empty\r\n\"a,b\",\"line1\r\nline2\",\r\n\"quote\"\"here\",x,", ',');
        Assert.Equal(3, rows.Count); Assert.Equal("a,b", rows[1][0]); Assert.Equal("line1\r\nline2", rows[1][1]);
        Assert.Equal("", rows[1][2]); Assert.Equal("quote\"here", rows[2][0]); Assert.Equal("", rows[2][2]);
        string json = DataConversion.Convert("x,y\n1,2", ToolKind.CsvConvert, new() { Format = "json", HasHeader = false });
        Assert.Equal(2, JsonNode.Parse(json)!.AsArray().Count);
    }

    [Theory]
    [InlineData("a,b\n1")]
    [InlineData("a,b\n\"unclosed,x")]
    [InlineData("a,b\n\"a\"x,b")]
    public void CsvRejectsMalformedData(string data) => Assert.Throws<FormatException>(() => DataConversion.ParseCsv(data, ','));

    [Fact]
    public void CsvRejectsDuplicateHeadersAndEscapesHtml()
    {
        Assert.Throws<FormatException>(() => DataConversion.Convert("a,a\n1,2", ToolKind.CsvConvert, new() { Format = "json" }));
        string html = DataConversion.Convert("h\n<script>alert(1)</script>", ToolKind.CsvConvert, new() { Format = "html" });
        Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
        string markdown = DataConversion.Convert("a\n<x|y>", ToolKind.CsvConvert, new() { Format = "md" });
        Assert.Contains("&lt;x&#124;y&gt;", markdown);
    }

    [Fact]
    public void JsonCsvAndXmlPreserveKeysAndNestedValues()
    {
        var o = new ToolOptions { Format = "csv" };
        var csv = DataConversion.Convert("[{\"x y\":\"a,b\",\"nest\":{\"x\":1}},{\"other\":true}]", ToolKind.JsonConvert, o);
        var rows = DataConversion.ParseCsv(csv, ',');
        Assert.Equal(new[] { "x y", "nest", "other" }, rows[0]); Assert.Equal("a,b", rows[1][0]);
        Assert.Equal("{\"x\":1}", rows[1][1]);
        string xml = DataConversion.Convert("{\"bad key <\":\"<&\"}", ToolKind.JsonConvert, o with { Format = "xml" });
        var tree = System.Xml.Linq.XElement.Parse(xml);
        Assert.Equal("bad key <", (string?)tree.Element("property")!.Attribute("name")); Assert.Equal("<&", tree.Element("property")!.Value);
    }

    [Fact]
    public void IniHandlesSectionsAndRejectsLossySyntax()
    {
        var json = DataConversion.ParseIni("; comment\nroot=value\n[section]\nkey=a=b # content\n");
        Assert.Equal("value", (string?)json["root"]); Assert.Equal("a=b # content", (string?)json["section"]!["key"]);
        Assert.Throws<FormatException>(() => DataConversion.ParseIni("a=1\na=2"));
        Assert.Throws<FormatException>(() => DataConversion.ParseIni("key value"));
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-8 BOM")]
    [InlineData("UTF-16 LE")]
    [InlineData("UTF-16 BE")]
    [InlineData("Shift_JIS")]
    public void EncodingsRoundTripWithoutLoss(string encoding)
    {
        const string text = "日本語\r\nabc";
        Assert.Equal(text, DataConversion.Decode(DataConversion.Encode(text, encoding), encoding));
        Assert.Throws<EncoderFallbackException>(() => DataConversion.Encode("😀", "Shift_JIS"));
        Assert.Throws<DecoderFallbackException>(() => DataConversion.Decode([0xff, 0xfe, 0x41], "UTF-8"));
    }

    [Theory]
    [InlineData("LF", "a\nb\nc\nd")]
    [InlineData("CRLF", "a\r\nb\r\nc\r\nd")]
    public void LineEndingsDoNotMultiply(string code, string expected) => Assert.Equal(expected, DataConversion.Newlines("a\r\nb\rc\nd", code));

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("file:stream")]
    [InlineData("a/CON.txt")]
    [InlineData("a./b")]
    [InlineData("a\\..\\b")]
    [InlineData("\\\\server\\share")]
    public void ArchivePathsCannotEscapeOrUseDeviceNames(string path) => Assert.Throws<IOException>(() => SafeOutput.EntryPath(_root, path));

    [Fact]
    public void OutputNeverOverwritesAndRemovesOnlyItsStaging()
    {
        string original = FileAt("a.txt"); string work;
        using (var stage = new SafeOutput(_root))
        {
            work = stage.WorkPath; string prepared = Path.Combine(work, "x.txt"); File.WriteAllText(prepared, "new");
            string saved = stage.PublishFile(prepared, "a.txt");
            Assert.Equal("a (2).txt", Path.GetFileName(saved)); Assert.Equal("new", File.ReadAllText(saved));
            Assert.Throws<IOException>(() => stage.PublishFile(original, "other.txt"));
        }
        Assert.False(Directory.Exists(work)); Assert.Equal("original", File.ReadAllText(original));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    public async Task ArchivesRoundTripAndPreserveEmptyDirectories(string format)
    {
        string source = Path.Combine(_root, "folder"); Directory.CreateDirectory(Path.Combine(source, "empty")); File.WriteAllText(Path.Combine(source, "日本語.txt"), "data");
        string archive = Path.Combine(_root, "archive." + format); string target = Path.Combine(_root, "target");
        await SafeArchives.CreateAsync([source], archive, format, CancellationToken.None);
        if (format == "zip") await SafeArchives.ExtractZipAsync(archive, target, CancellationToken.None);
        else await SafeArchives.ExtractTarAsync(archive, target, format == "tar.gz", CancellationToken.None);
        Assert.Equal("data", File.ReadAllText(Path.Combine(target, "folder", "日本語.txt"))); Assert.True(Directory.Exists(Path.Combine(target, "folder", "empty")));
    }

    [Fact]
    public async Task MaliciousArchiveFailsBeforeWritingOutside()
    {
        string archive = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("../outside.txt").Open()); w.Write("bad"); }
        string target = Path.Combine(_root, "target");
        await Assert.ThrowsAsync<IOException>(() => SafeArchives.ExtractZipAsync(archive, target, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task ArchivesRespectCancellationAndReadPermissions()
    {
        string source = FileAt("a.txt"); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SafeArchives.CreateAsync([source], Path.Combine(_root, "c.zip"), "zip", cts.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SafeArchives.CreateAsync([source], Path.Combine(_root, "d.zip"), "zip", CancellationToken.None, _ => false));
    }

    [Theory]
    [InlineData("Path = ../outside\nSize = 2")]
    [InlineData("Path = file\nSize = 21474836481")]
    [InlineData("Path = link\nSize = 0\nSymbolic Link = ../outside")]
    [InlineData("Path = link\nSize = 0\nAttributes = A_ lrwxrwxrwx")]
    public void SevenZipRejectsDangerousListings(string entries) => Assert.Throws<IOException>(() => SafeArchives.ValidateSevenZipListing("header\n----------\n" + entries, _root));

    [Fact]
    public void SevenZipAcceptsNormalEntries() => SafeArchives.ValidateSevenZipListing("header\n----------\nPath = folder/file.txt\nSize = 2\nAttributes = A", _root);

    [Fact]
    public async Task ArchivingIntoTheInputFolderDoesNotIncludeOrRecurseIntoStaging()
    {
        FileAt("data.txt");
        using var stage = new SafeOutput(_root);
        var result = await new ToolEngine(new()).ExecuteAsync([_root], ToolKind.ArchiveCreate, new() { Format = "zip" }, stage.WorkPath, CancellationToken.None);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("data.txt"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(".FileBox-"));
    }

    [Fact]
    public async Task SvgExternalReferencesAreRejectedBeforeRunningAnEngine()
    {
        var svg = FileAt("unsafe.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/secret\"/></svg>");
        using var stage = new SafeOutput(_root);
        await Assert.ThrowsAsync<IOException>(() => new ToolEngine(new()).ExecuteAsync([svg], ToolKind.ImageConvert, new() { Format = "png" }, stage.WorkPath, CancellationToken.None));
    }

    [Fact]
    public void FolderReportsAreBoundedAndDoNotIncludeRootInDeletion()
    {
        string empty = Path.Combine(_root, "empty"); Directory.CreateDirectory(empty);
        string nonempty = Path.Combine(_root, "full"); Directory.CreateDirectory(nonempty); File.WriteAllText(Path.Combine(nonempty, "x"), "x");
        var report = FolderInspection.EmptyFolders(_root, CancellationToken.None);
        Assert.Equal(new[] { empty }, report.Paths); Assert.DoesNotContain(_root, report.Paths);
        string tree = FolderInspection.Tree(_root, 1, CancellationToken.None); Assert.Contains("empty/", tree); Assert.DoesNotContain("    x", tree);
    }

    [Fact]
    public async Task PdfMergePreservesOrderAndOriginals()
    {
        string a = Path.Combine(_root, "a.pdf"), b = Path.Combine(_root, "b.pdf");
        using (var doc = new PdfDocument()) { doc.AddPage().Width = PdfSharp.Drawing.XUnit.FromPoint(200); doc.Save(a); }
        using (var doc = new PdfDocument()) { doc.AddPage().Width = PdfSharp.Drawing.XUnit.FromPoint(300); doc.Save(b); }
        byte[] original = File.ReadAllBytes(a);
        using var stage = new SafeOutput(_root);
        var output = await new ToolEngine(new()).ExecuteAsync([b, a], ToolKind.PdfMerge, new() { Format = "pdf" }, stage.WorkPath, CancellationToken.None);
        using var merged = PdfReader.Open(output.Path, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount); Assert.Equal(300, merged.Pages[0].Width.Point); Assert.Equal(200, merged.Pages[1].Width.Point);
        Assert.Equal(original, File.ReadAllBytes(a));
    }

    [Fact]
    public void VideoCommandsAreCultureIndependentAndNeverUseAShell()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
            var args = MediaCommands.Media("C:\\a & b.mp4", "C:\\new.mp4", ToolKind.VideoTrim, new() { Format = "mp4", StartSeconds = 1.25, EndSeconds = 3.5 });
            Assert.Contains("1.25", args); Assert.Contains("2.25", args); Assert.Contains("C:\\a & b.mp4", args); Assert.Contains("-n", args); Assert.Contains("file,pipe", args);
            Assert.Contains("libx264", args);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void InvalidOptionsAndIncompatibleCodecsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new ToolOptions { Format = "webm" }.Validate(ToolKind.VideoConvert));
        Assert.Throws<ArgumentException>(() => new ToolOptions { Format = "mp4", TargetMiB = double.NaN }.Validate(ToolKind.VideoConvert));
        Assert.Throws<ArgumentException>(() => new ToolOptions { Format = "mp4", StartSeconds = 5, EndSeconds = 3 }.Validate(ToolKind.VideoTrim));
        Assert.Throws<ArgumentException>(() => new ToolOptions { Format = "png", Background = "#fff -delete" }.Validate(ToolKind.ImageRotate));
        Assert.Throws<ArgumentException>(() => new ToolOptions { Format = "mp4", TargetMiB = 1 }.Validate(ToolKind.VideoCompress));
    }

    [Fact]
    public void ImageOutputUsesAnExplicitCoderInsteadOfSilentlyChangingFormat()
    {
        var args = MediaCommands.Image("PNG:input.png[0]", "result.heic", ToolKind.ImageConvert, new() { Format = "heic" });
        Assert.Equal("HEIC:result.heic", args[^1]);
        Assert.Equal("MSVG", MediaCommands.ImageCoder(".svg"));
    }

    [Fact]
    public void PageRangesValidateAndDeduplicate()
    {
        Assert.Equal(new[] { 1, 3, 4, 5 }, DataConversion.PageRange("1,3-5,3", 5));
        Assert.Equal(new[] { 1, 2 }, DataConversion.PageRange("", 2));
        Assert.Throws<ArgumentException>(() => DataConversion.PageRange("0,3", 5));
        Assert.Throws<ArgumentException>(() => DataConversion.PageRange("1-8", 5));
    }

    [Fact]
    public void EngineAndShellSettingsCannotBeImportedAsExecutableConfiguration()
    {
        var package = new SettingsPackage { Settings = new() { ["tools.engine.ffmpeg"] = "evil.exe", ["tools.contextMenu"] = "1", ["tools.preset.ImageConvert"] = "{}" } };
        var imported = SettingsPackage.Parse(package.ToJson(), out var error)!;
        Assert.Null(error); Assert.Single(imported.Settings); Assert.Contains("tools.preset.ImageConvert", imported.Settings.Keys);
    }

    [Fact]
    public async Task ExternalProcessCancelsAndDoesNotLeaveProcessRunning()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalProcess.RunAsync(ping, ["-t", "127.0.0.1"], _root, cancel.Token));
    }
}
