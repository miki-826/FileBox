using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileBox.Core.Settings;
using Xunit;

namespace FileBox.Tests;

/// <summary>英語表示の対応表（src/FileBox.App/Assets/Strings/en.json）と、その照合の仕組みの確認。</summary>
public sealed class TranslationTests
{
    static readonly string RepoRoot = FindRoot();

    /// <summary>画面に出ない文字列（例外の内部メッセージ・正規表現・書き出すファイル名など）。訳さない。</summary>
    static readonly HashSet<string> NotShown = new(StringComparer.Ordinal)
    {
        "ExplorerIntegration.Initialize が未実行", "InboxService.Initialize が未実行", "LinkService.Initialize が未実行",
        "SaveDialogWatcher.Initialize が未実行", "SettingsDialog.Store が未設定", "SuggestionService.Initialize が未実行",
        "日時,アカウント,操作,元,先,バイト,結果,詳細", "環境.txt",
        "英語の文字列を読み込めませんでした。日本語で表示します", "表示の設定を読み込めませんでした",
    };

    static readonly string[] CoreFiles =
    {
        "src/FileBox.Core/Files/FriendlyError.cs", "src/FileBox.Core/Files/BatchRename.cs", "src/FileBox.Core/Files/NameRule.cs",
        "src/FileBox.Core/Files/CleanupCandidates.cs", "src/FileBox.Core/Files/OperationJournal.cs", "src/FileBox.Core/Files/DateGroup.cs",
        "src/FileBox.Core/Search/SearchFacets.cs", "src/FileBox.Core/Search/QueryHints.cs", "src/FileBox.Core/Settings/SettingsPackage.cs",
        "src/FileBox.Core/Tools/ToolCatalog.cs", "src/FileBox.Core/Tools/DataConversion.cs", "src/FileBox.Core/Tools/MediaCommands.cs",
        "src/FileBox.Core/Tools/SafeOutput.cs", "src/FileBox.Core/Tools/SafeArchives.cs", "src/FileBox.Core/Tools/ExternalProcess.cs",
        "src/FileBox.Core/Tools/FolderInspection.cs", "src/FileBox.Tools/ToolEngine.cs",
    };

    [Fact]
    public void TranslatesExactPatternsAndPieces()
    {
        var t = new Translator(new Dictionary<string, string>
        {
            ["開く"] = "Open",
            ["今日"] = "Today",
            ["{0} 件を「{1}」へ移動しました"] = "Moved {0} items to \"{1}\"",
            ["{0} 件の{1}"] = "{1} of {0} items",
            ["移動"] = "move",
            ["戻る"] = "Back",
        });
        Assert.Equal("Open", t.Translate("開く"));
        Assert.Equal("Moved 3 items to \"資料\"", t.Translate("3 件を「資料」へ移動しました"));
        Assert.Equal("move of 2 items", t.Translate("2 件の移動"));          // 値も訳す
        Assert.Equal("Open\nBack", t.Translate("開く\n戻る"));              // 行ごと
        Assert.Equal("Today（3）", t.Translate("今日（3）"));                // 語ごと
        Assert.Null(t.Translate("未登録の文"));
    }

    [Fact]
    public void DictionaryIsValid()
    {
        var map = LoadDictionary();
        Assert.True(map.Count > 900);
        foreach (var (ja, en) in map)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), $"訳が空です: {ja}");
            Assert.True(Translator.Placeholders(ja).SequenceEqual(Translator.Placeholders(en)), $"{{0}} などの数が合いません: {ja} → {en}");
            Assert.False(Translator.HasJapanese(en) && en != "日本語" && !en.StartsWith("言語"), $"訳に日本語が残っています: {ja} → {en}");
        }
    }

    [Fact]
    public void EveryShownStringHasEnglish()
    {
        var map = LoadDictionary();
        var missing = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "FileBox.App"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Concat(CoreFiles.Select(f => Path.Combine(RepoRoot, f)));
        foreach (var file in files)
        {
            IEnumerable<string> texts = file.EndsWith(".cs") ? PlainLiterals(File.ReadAllText(file))
                : file.EndsWith(".xaml") ? XamlTexts(File.ReadAllText(file)) : Enumerable.Empty<string>();
            foreach (var text in texts)
            {
                if (!Translator.HasJapanese(text) || NotShown.Contains(text.Trim())) continue;
                foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(Translator.HasJapanese))
                    if (!map.ContainsKey(line) && !map.ContainsKey(text)) missing.Add($"{Path.GetFileName(file)}: {line}");
            }
        }
        Assert.True(missing.Count == 0, "en.json に無い文字列:\n" + string.Join("\n", missing.Distinct().Take(50)));
    }

    static Dictionary<string, string> LoadDictionary() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(RepoRoot, "src", "FileBox.App", "Assets", "Strings", "en.json")))!;

    /// <summary>補間でもなく生文字列でもない、ふつうの文字列リテラル（コメントの中は除く）。</summary>
    static IEnumerable<string> PlainLiterals(string src)
    {
        int i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/') { i = src.IndexOf('\n', i) is int n && n >= 0 ? n : src.Length; continue; }
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*') { i = src.IndexOf("*/", i + 2, StringComparison.Ordinal) is int n && n >= 0 ? n + 2 : src.Length; continue; }
            if (src[i] == '\'') { i++; while (i < src.Length && src[i] != '\'') i += src[i] == '\\' ? 2 : 1; i++; continue; }
            if (src[i] != '"') { i++; continue; }
            bool interpolated = i > 0 && (src[i - 1] == '$' || (src[i - 1] == '@' && i > 1 && src[i - 2] == '$'));
            bool verbatim = i > 0 && src[i - 1] == '@';
            if (src.AsSpan(i).StartsWith("\"\"\""))
            {
                int end = src.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 3;
                continue;
            }
            var sb = new StringBuilder();
            int j = i + 1;
            int holes = 0;
            while (j < src.Length)
            {
                char c = src[j];
                if (interpolated && c == '{') { if (j + 1 < src.Length && src[j + 1] == '{') { j += 2; continue; } holes++; }
                if (interpolated && c == '}' && holes > 0) { holes--; j++; continue; }
                if (interpolated && holes > 0) { if (c == '"') { j++; while (j < src.Length && src[j] != '"') j += src[j] == '\\' ? 2 : 1; } j++; continue; }
                if (verbatim)
                {
                    if (c == '"') { if (j + 1 < src.Length && src[j + 1] == '"') { sb.Append('"'); j += 2; continue; } break; }
                    sb.Append(c); j++; continue;
                }
                if (c == '\\' && j + 1 < src.Length)
                {
                    char e = src[j + 1];
                    if (e == 'u' && j + 5 < src.Length) { sb.Append((char)Convert.ToInt32(src.Substring(j + 2, 4), 16)); j += 6; continue; }
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    j += 2;
                    continue;
                }
                if (c == '"' || c == '\n') break;
                sb.Append(c);
                j++;
            }
            if (!interpolated) yield return sb.ToString();
            i = j + 1;
        }
    }

    static readonly Regex XamlAttribute = new(@"\b(Text|Content|Header|PlaceholderText|ToolTipService\.ToolTip|AutomationProperties\.Name|Label|Title|Message)=""([^""]*)""");

    static IEnumerable<string> XamlTexts(string xaml)
    {
        xaml = Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);
        foreach (Match m in XamlAttribute.Matches(xaml))
            if (!m.Groups[2].Value.StartsWith('{')) yield return System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FileBox.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("FileBox.sln が見つかりません");
    }
}
