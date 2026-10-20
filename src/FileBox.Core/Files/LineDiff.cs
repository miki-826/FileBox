namespace FileBox.Core.Files;

public enum DiffKind { Same, Removed, Added }

/// <summary>差分の 1 行。LeftLine / RightLine は 1 始まりの行番号（その側に無い行は null）。</summary>
public sealed record DiffLine(DiffKind Kind, string Text, int? LeftLine, int? RightLine);

/// <summary>テキストの行単位の差分（最長共通部分列）。比較の前後の一致する行は先に除いて計算量を抑える。</summary>
public static class LineDiff
{
    /// <summary>この行数（共通の前後を除いた残り同士の積）を超えると、全体を「削除＋追加」として返す。</summary>
    public const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        int start = 0;
        while (start < left.Count && start < right.Count && left[start] == right[start]) start++;
        int endL = left.Count, endR = right.Count;
        while (endL > start && endR > start && left[endL - 1] == right[endR - 1]) { endL--; endR--; }

        var result = new List<DiffLine>();
        for (int i = 0; i < start; i++) result.Add(new(DiffKind.Same, left[i], i + 1, i + 1));

        int n = endL - start, m = endR - start;
        if ((long)n * m > MaxCells)
        {
            for (int i = start; i < endL; i++) result.Add(new(DiffKind.Removed, left[i], i + 1, null));
            for (int j = start; j < endR; j++) result.Add(new(DiffKind.Added, right[j], null, j + 1));
        }
        else
        {
            // lcs[i, j] = left[start+i..] と right[start+j..] の最長共通部分列の長さ
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = left[start + i] == right[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int a = 0, b = 0;
            while (a < n || b < m)
            {
                if (a < n && b < m && left[start + a] == right[start + b])
                {
                    result.Add(new(DiffKind.Same, left[start + a], start + a + 1, start + b + 1));
                    a++; b++;
                }
                else if (b < m && (a == n || lcs[a, b + 1] >= lcs[a + 1, b]))
                {
                    result.Add(new(DiffKind.Added, right[start + b], null, start + b + 1));
                    b++;
                }
                else
                {
                    result.Add(new(DiffKind.Removed, left[start + a], start + a + 1, null));
                    a++;
                }
            }
        }

        for (int i = endL, j = endR; i < left.Count; i++, j++) result.Add(new(DiffKind.Same, left[i], i + 1, j + 1));
        return result;
    }

    public static IReadOnlyList<DiffLine> Compare(string left, string right) => Compare(Lines(left), Lines(right));

    static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');
}
