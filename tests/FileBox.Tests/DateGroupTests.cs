using FileBox.Core.Files;
using Xunit;

namespace FileBox.Tests;

public class DateGroupTests
{
    // 2026-10-21 は水曜日。日曜始まりなら今週は 10/18〜、先週は 10/11〜10/17
    static readonly DateTime Now = new(2026, 10, 21, 15, 0, 0);

    [Theory]
    [InlineData("2026-10-21 08:00", DateGroupKind.Today)]
    [InlineData("2026-10-22 09:00", DateGroupKind.Today)]
    [InlineData("2026-10-20 23:59", DateGroupKind.Yesterday)]
    [InlineData("2026-10-18 10:00", DateGroupKind.ThisWeek)]
    [InlineData("2026-10-17 10:00", DateGroupKind.LastWeek)]
    [InlineData("2026-10-11 00:00", DateGroupKind.LastWeek)]
    [InlineData("2026-10-10 10:00", DateGroupKind.ThisMonth)]
    [InlineData("2026-09-05 10:00", DateGroupKind.LastMonth)]
    [InlineData("2026-03-01 10:00", DateGroupKind.ThisYear)]
    [InlineData("2025-12-31 10:00", DateGroupKind.Older)]
    public void ClassifiesLikeExplorer(string value, DateGroupKind expected) =>
        Assert.Equal(expected, DateGroup.Classify(DateTime.Parse(value), Now, DayOfWeek.Sunday));

    [Fact]
    public void WeekStartFollowsRegion()
    {
        // 月曜始まりなら日曜（10/18）は先週
        Assert.Equal(DateGroupKind.LastWeek, DateGroup.Classify(new DateTime(2026, 10, 18), Now, DayOfWeek.Monday));
        Assert.Equal(DateGroupKind.Unknown, DateGroup.Classify(null, Now, DayOfWeek.Sunday));
        Assert.Equal("先週", DateGroup.Label(DateGroupKind.LastWeek));
    }
}
