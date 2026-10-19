using System.Text.RegularExpressions;

namespace FileBox.Core.Files;

/// <summary>
/// 振り分けルールの条件（名前のパターンと拡張子）。パターンは * と ? が使え、* を含まない語は「名前に含む」として扱う。
/// 拡張子はカンマ区切りで複数指定できる。条件が 1 つも無いルールはどのファイルにも当てはまらない。
/// </summary>
public static class NameRule
{
    public static bool Matches(string? namePattern, string? extensions, string fileName)
    {
        bool hasExt = !string.IsNullOrWhiteSpace(extensions);
        bool hasName = !string.IsNullOrWhiteSpace(namePattern);
        if (!hasExt && !hasName) return false;
        if (hasExt)
        {
            var exts = extensions!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => "." + e.TrimStart('*').TrimStart('.'));
            if (!exts.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (hasName)
        {
            string pattern = namePattern!.Trim();
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0) return fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (!Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return false;
        }
        return true;
    }

    /// <summary>条件の説明（例:「名前に“請求書”を含む・PDF」）。</summary>
    public static string Describe(string? namePattern, string? extensions)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(namePattern))
        {
            string p = namePattern.Trim();
            string word = p.Trim('*');
            parts.Add(p.IndexOfAny(new[] { '*', '?' }) < 0 || (p.StartsWith('*') && p.EndsWith('*') && word.IndexOfAny(new[] { '*', '?' }) < 0)
                ? $"名前に「{word}」を含む"
                : $"名前が「{p}」");
        }
        if (!string.IsNullOrWhiteSpace(extensions)) parts.Add(extensions.ToUpperInvariant().Replace(",", "・"));
        return parts.Count == 0 ? "（条件なし）" : string.Join("・", parts);
    }
}
