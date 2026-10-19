using FileBox.Core.Files;
using Xunit;

namespace FileBox.Tests;

public sealed class FileOperationTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "filebox-ops-" + Guid.NewGuid().ToString("N"));

    public FileOperationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UniqueNameSkipsExistingNames()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a\報告書.docx", @"C:\a\報告書 (2).docx" };
        Assert.Equal(@"C:\a\報告書 (3).docx", UniqueName.Path(@"C:\a\報告書.docx", taken.Contains));
        Assert.Equal(@"C:\a\新規.txt", UniqueName.Path(@"C:\a\新規.txt", taken.Contains));
        Assert.Equal(@"C:\a\報告書 - コピー.docx", UniqueName.Copy(@"C:\a\報告書.docx", taken.Contains));
    }

    [Fact]
    public void UniqueNameKeepsDotsInFolderNames()
    {
        string dir = Path.Combine(_root, "v1.2");
        Directory.CreateDirectory(dir);
        Assert.Equal(Path.Combine(_root, "v1.2 (2)"), UniqueName.Path(dir));
    }

    [Fact]
    public void InverseOperations()
    {
        var move = new FileOperation(OperationKind.Move, new[] { ("a", "x\\a"), ("b", "x\\b") });
        var inv = move.Inverse();
        Assert.Equal(OperationKind.Move, inv.Kind);
        Assert.Equal(new[] { ("x\\b", "b"), ("x\\a", "a") }, inv.Items);

        var copy = new FileOperation(OperationKind.Copy, new[] { ("a", "a - コピー") });
        Assert.Equal(OperationKind.Recycle, copy.Inverse().Kind);
        Assert.Equal("a - コピー", copy.Inverse().Items[0].From);
        Assert.Equal(OperationKind.Restore, copy.Inverse().Inverse().Kind);
        Assert.Equal(OperationKind.RemoveFolder, new FileOperation(OperationKind.CreateFolder, new[] { ("f", "f") }).Inverse().Kind);
        Assert.Equal("2 件の移動", move.Describe());
    }

    [Fact]
    public async Task JournalUndoRedo()
    {
        var journal = new OperationJournal(capacity: 2);
        var executed = new List<FileOperation>();
        Task<FileOperation?> Run(FileOperation op) { executed.Add(op); return Task.FromResult<FileOperation?>(op); }

        journal.Record(new FileOperation(OperationKind.Rename, new[] { ("a", "b") }));
        journal.Record(new FileOperation(OperationKind.Rename, new[] { ("b", "c") }));
        journal.Record(new FileOperation(OperationKind.Rename, new[] { ("c", "d") }));   // 古い 1 件は捨てられる

        await journal.UndoAsync(Run);
        Assert.Equal(("d", "c"), executed[^1].Items[0]);
        Assert.True(journal.CanRedo);
        await journal.RedoAsync(Run);
        Assert.Equal(("c", "d"), executed[^1].Items[0]);
        Assert.False(journal.CanRedo);

        await journal.UndoAsync(Run);
        await journal.UndoAsync(Run);
        Assert.False(journal.CanUndo);
        Assert.Null(await journal.UndoAsync(Run));

        // 新しく記録するとやり直しは消える
        journal.Record(new FileOperation(OperationKind.Rename, new[] { ("x", "y") }));
        Assert.False(journal.CanRedo);
    }

    [Fact]
    public async Task JournalDropsFailedUndo()
    {
        var journal = new OperationJournal();
        journal.Record(new FileOperation(OperationKind.Move, new[] { ("a", "b") }));
        await journal.UndoAsync(_ => Task.FromResult<FileOperation?>(null));
        Assert.False(journal.CanUndo);
        Assert.False(journal.CanRedo);
    }

    [Fact]
    public void BatchRenameNumberingAndReplace()
    {
        var paths = new[] { @"C:\p\IMG_001.jpg", @"C:\p\IMG_002.jpg", @"C:\p\IMG_003.jpg" };
        var rule = new RenameRule { Numbering = NumberPosition.ReplaceName, NumberBase = "旅行", NumberDigits = 3 };
        var plans = BatchRename.Preview(paths, rule, _ => null, _ => false);
        Assert.Equal(new[] { "旅行_001.jpg", "旅行_002.jpg", "旅行_003.jpg" }, plans.Select(p => p.NewName));
        Assert.All(plans, p => Assert.Null(p.Error));

        var replace = BatchRename.Preview(paths, new RenameRule { Find = "img_", Replace = "photo-" }, _ => null, _ => false);
        Assert.Equal("photo-001.jpg", replace[0].NewName);

        var regex = BatchRename.Preview(paths, new RenameRule { Find = @"IMG_(\d+)", Replace = "$1_写真", UseRegex = true }, _ => null, _ => false);
        Assert.Equal("001_写真.jpg", regex[0].NewName);

        var date = BatchRename.Preview(paths[..1], new RenameRule { DateFormat = "yyyyMMdd" }, _ => new DateTime(2026, 10, 20), _ => false);
        Assert.Equal("20261020_IMG_001.jpg", date[0].NewName);

        var upper = BatchRename.Preview(new[] { @"C:\p\report.txt" }, new RenameRule { Case = NameCase.Upper }, _ => null, _ => false);
        Assert.Equal("REPORT.txt", upper[0].NewName);
    }

    [Fact]
    public void BatchRenameDetectsProblems()
    {
        var paths = new[] { @"C:\p\a.txt", @"C:\p\b.txt" };
        var same = BatchRename.Preview(paths, new RenameRule { Numbering = NumberPosition.ReplaceName, NumberBase = "x", NumberDigits = 1, NumberStart = 1 }, _ => null, _ => false);
        Assert.All(same, p => Assert.Null(p.Error));

        var clash = BatchRename.Preview(paths, new RenameRule { Find = "a", Replace = "c" }, _ => null, p => p.EndsWith("c.txt"));
        Assert.Equal("同じ名前のファイルが既にあります", clash[0].Error);

        // b → a は、名前が変わらない a と重なる
        bool Both(string p) => p is @"C:\p\a.txt" or @"C:\p\b.txt";
        var dup = BatchRename.Preview(paths, new RenameRule { Find = "b", Replace = "a" }, _ => null, Both);
        Assert.Equal("同じ名前のファイルが既にあります", dup[1].Error);
        // a → b と b → a の入れ替えは問題ない
        var swap = BatchRename.Preview(paths, new RenameRule { Find = @"^(a|b)$", Replace = "${1}x", UseRegex = true }, _ => null, Both);
        Assert.All(swap, p => Assert.Null(p.Error));
        var caseOnly = BatchRename.Preview(paths, new RenameRule { Case = NameCase.Upper }, _ => null, Both);
        Assert.All(caseOnly, p => Assert.Null(p.Error));

        var invalid = BatchRename.Preview(paths, new RenameRule { Find = "a", Replace = "a:b" }, _ => null, _ => false);
        Assert.NotNull(invalid[0].Error);
        Assert.NotNull(BatchRename.Validate("CON.txt"));
        Assert.NotNull(BatchRename.Preview(paths, new RenameRule { Find = "(", UseRegex = true }, _ => null, _ => false)[0].Error);
    }

    [Fact]
    public async Task FindsDuplicates()
    {
        string a = Path.Combine(_root, "dup");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        var big = new byte[200_000];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(a, "1.bin"), big);
        File.WriteAllBytes(Path.Combine(a, "sub", "2.bin"), big);
        var other = (byte[])big.Clone();
        other[^1] ^= 0xFF;   // 先頭 64KB は同じで末尾だけ違う
        File.WriteAllBytes(Path.Combine(a, "3.bin"), other);
        File.WriteAllText(Path.Combine(a, "x.txt"), "hello");
        File.WriteAllText(Path.Combine(a, "y.txt"), "hello");
        File.WriteAllText(Path.Combine(a, "z.txt"), "world");

        var groups = await DuplicateFinder.FindAsync(new[] { a });
        Assert.Equal(2, groups.Count);
        Assert.Equal(200_000, groups[0].Wasted);
        Assert.Equal(new[] { "1.bin", "2.bin" }, groups[0].Paths.Select(Path.GetFileName));
        Assert.Equal(new[] { "x.txt", "y.txt" }, groups[1].Paths.Select(Path.GetFileName));
    }

    [Fact]
    public void CleanupFindsOldInstallersAndExtractedArchives()
    {
        string dl = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(Path.Combine(dl, "資料"));
        var now = new DateTime(2026, 10, 20);
        void Make(string name, DateTime time)
        {
            string p = Path.Combine(dl, name);
            File.WriteAllText(p, "x");
            File.SetCreationTime(p, time);
            File.SetLastWriteTime(p, time);
        }
        Make("setup.exe", now.AddDays(-40));
        Make("new-setup.msi", now.AddDays(-3));
        Make("資料.zip", now.AddDays(-1));
        Make("古いメモ.txt", now.AddDays(-400));
        Make("最近.txt", now.AddDays(-2));

        var found = CleanupCandidates.ScanDownloads(dl, now);
        Assert.Equal(new[] { "setup.exe", "古いメモ.txt", "資料.zip" }, found.Select(c => Path.GetFileName(c.Path)).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(CleanupKind.ExtractedArchive, found.Single(c => c.Path.EndsWith(".zip")).Kind);
        Assert.Equal(CleanupKind.OldInstaller, found.Single(c => c.Path.EndsWith(".exe")).Kind);

        string temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(Path.Combine(temp, "old"));
        File.WriteAllText(Path.Combine(temp, "old", "a.tmp"), "x");
        File.SetLastWriteTime(Path.Combine(temp, "old", "a.tmp"), now.AddDays(-30));
        File.SetCreationTime(Path.Combine(temp, "old", "a.tmp"), now.AddDays(-30));
        Directory.SetLastWriteTime(Path.Combine(temp, "old"), now.AddDays(-30));
        Directory.SetCreationTime(Path.Combine(temp, "old"), now.AddDays(-30));
        Directory.CreateDirectory(Path.Combine(temp, "busy"));
        File.WriteAllText(Path.Combine(temp, "busy", "b.tmp"), "x");
        File.SetLastWriteTime(Path.Combine(temp, "busy", "b.tmp"), now);
        File.SetCreationTime(Path.Combine(temp, "busy", "b.tmp"), now);
        Directory.SetCreationTime(Path.Combine(temp, "busy"), now.AddDays(-30));
        Directory.SetLastWriteTime(Path.Combine(temp, "busy"), now.AddDays(-30));
        var t = CleanupCandidates.ScanTemp(temp, now);
        Assert.Equal(new[] { "old" }, t.Select(c => Path.GetFileName(c.Path)));
    }

    [Fact]
    public void LineDiffFindsChanges()
    {
        var d = LineDiff.Compare("a\nb\nc\nd", "a\nc\nx\nd");
        Assert.Equal(new[] { DiffKind.Same, DiffKind.Removed, DiffKind.Same, DiffKind.Added, DiffKind.Same }, d.Select(l => l.Kind));
        Assert.Equal("b", d[1].Text);
        Assert.Equal(3, d[3].RightLine);
        Assert.All(LineDiff.Compare("same", "same"), l => Assert.Equal(DiffKind.Same, l.Kind));
    }

    [Fact]
    public async Task ComparesFolders()
    {
        string l = Path.Combine(_root, "L"), r = Path.Combine(_root, "R");
        Directory.CreateDirectory(l);
        Directory.CreateDirectory(r);
        File.WriteAllText(Path.Combine(l, "same.txt"), "1");
        File.WriteAllText(Path.Combine(r, "same.txt"), "1");
        File.WriteAllText(Path.Combine(l, "diff.txt"), "1");
        File.WriteAllText(Path.Combine(r, "diff.txt"), "2");
        File.WriteAllText(Path.Combine(l, "left.txt"), "1");
        File.WriteAllText(Path.Combine(r, "right.txt"), "1");
        var result = (await FolderCompare.CompareAsync(l, r)).ToDictionary(e => e.RelativePath, e => e.Status);
        Assert.Equal(CompareStatus.Same, result["same.txt"]);
        Assert.Equal(CompareStatus.Different, result["diff.txt"]);
        Assert.Equal(CompareStatus.OnlyLeft, result["left.txt"]);
        Assert.Equal(CompareStatus.OnlyRight, result["right.txt"]);
    }

    [Fact]
    public void ExplainsErrors()
    {
        var inUse = FriendlyError.Explain(new IOException("x", unchecked((int)0x80070020)));
        Assert.True(inUse.IsInUse);
        Assert.Contains("ほかのアプリ", inUse.Cause);
        Assert.Equal("アクセスが拒否されました", FriendlyError.Explain(new UnauthorizedAccessException()).Cause);
        Assert.Equal("ディスクの空き容量が足りません", FriendlyError.Explain(new IOException("x", unchecked((int)0x80070070))).Cause);
        Assert.Contains("共有フォルダー", FriendlyError.FromWin32(67).Cause);
    }

    [Fact]
    public void TreemapFillsArea()
    {
        var cells = Treemap.Layout(new double[] { 6, 6, 4, 3, 2, 2, 1, 0 }, 600, 400);
        Assert.Equal(7, cells.Count);
        Assert.Equal(600 * 400, cells.Sum(c => c.Width * c.Height), 3);
        Assert.All(cells, c => Assert.True(c.X >= -1e-9 && c.Y >= -1e-9 && c.X + c.Width <= 600 + 1e-6 && c.Y + c.Height <= 400 + 1e-6));
        // 面積は重みに比例
        Assert.Equal(6.0 / 24 * 600 * 400, cells.Single(c => c.Index == 0).Width * cells.Single(c => c.Index == 0).Height, 3);
    }

    [Fact]
    public void TypeAheadJumps()
    {
        var names = new[] { "apple.txt", "Banana", "バナナ.txt", "berry", "cherry" };
        var t = new TypeAhead();
        var now = DateTime.Now;
        Assert.Equal(1, t.Next('b', names, -1, now));
        Assert.Equal(3, t.Next('e', names, 1, now.AddMilliseconds(100)));         // "be" → berry
        Assert.Equal(4, t.Next('c', names, 3, now.AddSeconds(5)));                // 時間が空いたら打ち直し
        Assert.Equal(2, TypeAhead.Find(names, "ばな", 0));                         // ひらがなでカタカナの名前
        var r = new TypeAhead();
        Assert.Equal(1, r.Next('b', names, 0, now));
        Assert.Equal(3, r.Next('b', names, 1, now.AddMilliseconds(100)));        // 同じ文字の繰り返しで次へ
    }

    [Fact]
    public void NameRuleMatches()
    {
        Assert.True(NameRule.Matches("請求書", "pdf", "2026年10月_請求書.PDF"));
        Assert.False(NameRule.Matches("請求書", "pdf", "請求書.docx"));
        Assert.True(NameRule.Matches("*見積*", null, "見積書_山田.xlsx"));
        Assert.True(NameRule.Matches(null, "jpg, png", "a.png"));
        Assert.False(NameRule.Matches(null, null, "a.png"));
        Assert.Equal("名前に「請求書」を含む・PDF", NameRule.Describe("*請求書*", "pdf"));
    }

    [Fact]
    public async Task ArchivePlanAndPartialExtract()
    {
        string zip = Path.Combine(_root, "一つ.zip");
        string src = Path.Combine(_root, "src", "中身");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
        await ArchiveService.CompressAsync(new[] { src }, zip);

        var entries = ArchiveService.List(zip);
        Assert.Contains(entries, e => e.FullName == "中身/" && e.IsDirectory);
        Assert.Contains(entries, e => e.FullName == "中身/sub/b.txt" && e.Depth == 2);

        var (dest, strip) = ArchiveService.PlanExtract(zip);
        Assert.Equal(Path.Combine(_root, "中身"), dest);
        Assert.Equal("中身/", strip);
        await ArchiveService.ExtractEntriesAsync(zip, null, dest, strip);
        Assert.True(File.Exists(Path.Combine(_root, "中身", "sub", "b.txt")));

        // 同名のフォルダーがあれば「(2)」
        Assert.Equal(Path.Combine(_root, "中身 (2)"), ArchiveService.PlanExtract(zip).Destination);

        // 選んだものだけ
        string part = Path.Combine(_root, "part");
        await ArchiveService.ExtractEntriesAsync(zip, new[] { "中身/sub/" }, part);
        Assert.True(File.Exists(Path.Combine(part, "中身", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(part, "中身", "a.txt")));

        // ばらばらの中身はフォルダーを作る
        string loose = Path.Combine(_root, "ばらばら.zip");
        await ArchiveService.CompressAsync(new[] { Path.Combine(src, "a.txt"), Path.Combine(src, "sub") }, loose);
        Assert.Equal(Path.Combine(_root, "ばらばら"), ArchiveService.PlanExtract(loose).Destination);
    }

    [Fact]
    public void ExtractsOfficeAndText()
    {
        string txt = Path.Combine(_root, "sjis.txt");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(txt, System.Text.Encoding.GetEncoding(932).GetBytes("請求書の本文"));
        Assert.Equal("請求書の本文", TextExtractor.Extract(txt));
        File.WriteAllText(Path.Combine(_root, "u.md"), "# 見出し\n本文");
        Assert.Contains("本文", TextExtractor.Extract(Path.Combine(_root, "u.md")));

        string docx = Path.Combine(_root, "a.docx");
        using (var z = System.IO.Compression.ZipFile.Open(docx, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("word/document.xml").Open()))
            w.Write("<w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>見積の</w:t></w:r><w:r><w:t>お知らせ</w:t></w:r></w:p></w:body></w:document>");
        Assert.Contains("見積のお知らせ", TextExtractor.Extract(docx));
        Assert.Null(TextExtractor.Extract(Path.Combine(_root, "none.bin")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
