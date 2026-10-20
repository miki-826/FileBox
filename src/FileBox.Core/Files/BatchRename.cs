using System.Globalization;
using System.Text.RegularExpressions;

namespace FileBox.Core.Files;

public enum NameCase { Keep, Upper, Lower, Title }
public enum NumberPosition { None, Prefix, Suffix, ReplaceName }

/// <summary>一括の名前変更の規則。適用順は「置換 → 大文字小文字 → 連番 → 日付」。拡張子は既定で変えない。</summary>
public sealed record RenameRule
{
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public NameCase Case { get; init; }
    public NumberPosition Numbering { get; init; }
    public int NumberStart { get; init; } = 1;
    public int NumberDigits { get; init; } = 2;
    /// <summary>連番で名前を置き換えるときの土台（例: 「写真」→「写真_01」）。</summary>
    public string NumberBase { get; init; } = "";
    public string NumberSeparator { get; init; } = "_";
    /// <summary>更新日時を前に付ける書式（例: yyyyMMdd）。空なら付けない。</summary>
    public string DateFormat { get; init; } = "";
    public bool IncludeExtension { get; init; }
}

/// <summary>名前変更の予定 1 件。Error があるものは実行しない。</summary>
public sealed record RenamePlan(string Path, string OldName, string NewName, string? Error)
{
    public bool Changes => Error == null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
    public string NewPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", NewName);
}

/// <summary>一括の名前変更（連番・置換・日付の付加・大文字小文字）。実行前に全件の結果と問題を確認できる。</summary>
public static class BatchRename
{
    static readonly char[] Invalid = System.IO.Path.GetInvalidFileNameChars();
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// paths（並べた順に連番を振る）に rule を当てた結果。
    /// modified はファイルの更新日時、exists は変更後の名前が既にあるかの確認（テスト用に差し替えられる）。
    /// </summary>
    public static IReadOnlyList<RenamePlan> Preview(IReadOnlyList<string> paths, RenameRule rule,
        Func<string, DateTime?>? modified = null, Func<string, bool>? exists = null)
    {
        modified ??= p => File.Exists(p) ? File.GetLastWriteTime(p) : Directory.Exists(p) ? Directory.GetLastWriteTime(p) : null;
        exists ??= p => File.Exists(p) || Directory.Exists(p);

        Regex? regex = null;
        string? regexError = null;
        if (rule.UseRegex && rule.Find.Length > 0)
        {
            try { regex = new Regex(rule.Find, (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { regexError = $"検索の正規表現が正しくありません: {ex.Message}"; }
        }

        var plans = new List<RenamePlan>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i].TrimEnd('\\');
            string oldName = System.IO.Path.GetFileName(path);
            if (regexError != null) { plans.Add(new(path, oldName, oldName, regexError)); continue; }
            bool isDir = Directory.Exists(path);
            string stem = rule.IncludeExtension || isDir ? oldName : System.IO.Path.GetFileNameWithoutExtension(oldName);
            string ext = rule.IncludeExtension || isDir ? "" : System.IO.Path.GetExtension(oldName);
            try
            {
                string name = Apply(stem, rule, regex, rule.NumberStart + i, modified(path));
                string newName = (name + ext).TrimEnd(' ', '.');
                plans.Add(new(path, oldName, newName, Validate(newName)));
            }
            catch (RegexMatchTimeoutException)
            {
                plans.Add(new(path, oldName, oldName, "置換に時間がかかりすぎました。条件を見直してください"));
            }
        }

        // 変更後の名前の重なり（この一括の中どうし・既存のファイル）
        var targets = plans.Where(p => p.Changes).GroupBy(p => p.NewPath, StringComparer.OrdinalIgnoreCase);
        // 一括の中で名前が変わる項目の今の場所は空くので、そこへの変更は問題ない（実行時は一時名を経由して入れ替える）
        var vacated = new HashSet<string>(plans.Where(p => p.Changes).Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var result = plans.ToList();
        foreach (var g in targets)
        {
            bool duplicate = g.Count() > 1;
            bool clash = !duplicate && exists(g.Key) && !vacated.Contains(g.Key);
            if (!duplicate && !clash) continue;
            foreach (var p in g)
                result[result.IndexOf(p)] = p with { Error = duplicate ? "ほかの項目と同じ名前になります" : "同じ名前のファイルが既にあります" };
        }
        return result;
    }

    static string Apply(string stem, RenameRule rule, Regex? regex, int number, DateTime? date)
    {
        string name = stem;
        if (rule.Find.Length > 0)
        {
            name = regex != null
                ? regex.Replace(name, rule.Replace)
                : name.Replace(rule.Find, rule.Replace, rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        name = rule.Case switch
        {
            NameCase.Upper => name.ToUpperInvariant(),
            NameCase.Lower => name.ToLowerInvariant(),
            NameCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
            _ => name,
        };
        string num = number.ToString(new string('0', Math.Clamp(rule.NumberDigits, 1, 9)), CultureInfo.InvariantCulture);
        name = rule.Numbering switch
        {
            NumberPosition.Prefix => num + rule.NumberSeparator + name,
            NumberPosition.Suffix => name + rule.NumberSeparator + num,
            NumberPosition.ReplaceName => rule.NumberBase.Length > 0 ? rule.NumberBase + rule.NumberSeparator + num : num,
            _ => name,
        };
        if (rule.DateFormat.Length > 0 && date is DateTime d)
            name = d.ToString(rule.DateFormat, CultureInfo.InvariantCulture) + rule.NumberSeparator + name;
        return name;
    }

    /// <summary>Windows で使えない名前なら理由を返す。</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "名前が空になります";
        if (name.IndexOfAny(Invalid) >= 0) return "使えない文字（\\ / : * ? \" < > |）が含まれます";
        string stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) return $"「{stem}」は Windows で予約された名前です";
        if (name.Length > 255) return "名前が長すぎます（255 文字まで）";
        return null;
    }
}
