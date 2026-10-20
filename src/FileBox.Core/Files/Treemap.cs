namespace FileBox.Core.Files;

/// <summary>ツリーマップの 1 マス。Index は Layout に渡した並びでの位置。</summary>
public readonly record struct TreemapCell(int Index, double X, double Y, double Width, double Height);

/// <summary>
/// 容量のツリーマップ（squarified 方式: Bruls ほか 2000）。マスがなるべく正方形に近くなるように行を詰める。
/// </summary>
public static class Treemap
{
    /// <summary>weights（0 以下は除く）を幅 width・高さ height の領域に敷き詰める。</summary>
    public static IReadOnlyList<TreemapCell> Layout(IReadOnlyList<double> weights, double width, double height)
    {
        var cells = new List<TreemapCell>();
        if (width <= 0 || height <= 0) return cells;
        var items = weights.Select((w, i) => (Index: i, Weight: w)).Where(x => x.Weight > 0).OrderByDescending(x => x.Weight).ToList();
        double total = items.Sum(x => x.Weight);
        if (total <= 0) return cells;
        double scale = width * height / total;
        var areas = items.Select(x => (x.Index, Area: x.Weight * scale)).ToList();

        double x0 = 0, y0 = 0, w = width, h = height;
        int start = 0;
        while (start < areas.Count)
        {
            double side = Math.Min(w, h);
            int end = start + 1;
            double rowSum = areas[start].Area;
            double worst = Worst(areas, start, end, rowSum, side);
            while (end < areas.Count)
            {
                double next = Worst(areas, start, end + 1, rowSum + areas[end].Area, side);
                if (next > worst) break;
                worst = next;
                rowSum += areas[end].Area;
                end++;
            }
            // 短い辺に沿って 1 行を置き、残りの領域を縮める
            double thickness = rowSum / side;
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = areas[i].Area / thickness;
                cells.Add(w >= h
                    ? new TreemapCell(areas[i].Index, x0, y0 + offset, thickness, length)
                    : new TreemapCell(areas[i].Index, x0 + offset, y0, length, thickness));
                offset += length;
            }
            if (w >= h) { x0 += thickness; w -= thickness; }
            else { y0 += thickness; h -= thickness; }
            start = end;
        }
        return cells;
    }

    /// <summary>行 [start, end) を辺 side に沿って並べたときの、最も細長いマスの縦横比。</summary>
    static double Worst(List<(int Index, double Area)> areas, int start, int end, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, areas[i].Area);
            min = Math.Min(min, areas[i].Area);
        }
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
