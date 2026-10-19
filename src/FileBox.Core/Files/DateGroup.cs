namespace FileBox.Core.Files;

/// <summary>更新日時・受け取り日時のまとまり（エクスプローラーの「日付でグループ化」と同じ区切り）。並び順は新しい順。</summary>
public enum DateGroupKind { Today, Yesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisYear, Older, Unknown }

public static class DateGroup
{
    /// <summary>
    /// 日時がどのまとまりに入るか。週の始まりは地域の設定（日本は日曜）に合わせる。
    /// 未来の日時（時計のずれ・予約）は「今日」に含める。
    /// </summary>
    public static DateGroupKind Classify(DateTime? value, DateTime now, DayOfWeek firstDayOfWeek)
    {
        if (value is not { } v) return DateGroupKind.Unknown;
        DateTime today = now.Date;
        DateTime day = v.Date;
        if (day >= today) return DateGroupKind.Today;
        if (day == today.AddDays(-1)) return DateGroupKind.Yesterday;
        DateTime weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        if (day >= weekStart) return DateGroupKind.ThisWeek;
        if (day >= weekStart.AddDays(-7)) return DateGroupKind.LastWeek;
        DateTime monthStart = new(today.Year, today.Month, 1);
        if (day >= monthStart) return DateGroupKind.ThisMonth;
        if (day >= monthStart.AddMonths(-1)) return DateGroupKind.LastMonth;
        if (day.Year == today.Year) return DateGroupKind.ThisYear;
        return DateGroupKind.Older;
    }

    public static string Label(DateGroupKind kind) => kind switch
    {
        DateGroupKind.Today => "今日",
        DateGroupKind.Yesterday => "昨日",
        DateGroupKind.ThisWeek => "今週",
        DateGroupKind.LastWeek => "先週",
        DateGroupKind.ThisMonth => "今月",
        DateGroupKind.LastMonth => "先月",
        DateGroupKind.ThisYear => "今年",
        DateGroupKind.Older => "それ以前",
        _ => "日付なし",
    };
}
