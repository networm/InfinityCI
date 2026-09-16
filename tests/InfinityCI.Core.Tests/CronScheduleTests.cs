using InfinityCI.Core;
using Xunit;

namespace InfinityCI.Core.Tests;

public class CronScheduleTests
{
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 2 * * 1-5")]
    [InlineData("30 2 1,15 * *")]
    [InlineData("9-17/2 * * * *")]
    [InlineData("0 0 * * 0")]
    [InlineData("59 23 31 12 *")]
    public void Parse_AcceptsValidExpressions(string expression)
    {
        var schedule = CronSchedule.Parse(expression);
        // Any expression matching somewhere within a year must yield a next occurrence.
        var from = At(2026, 1, 5, 10, 4);
        Assert.True(schedule.NextOccurrence(from) > from);
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("61 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("0 0 32 1 *")]
    [InlineData("* * * * 8")]
    [InlineData("abc * * * *")]
    public void Parse_RejectsInvalidExpressions(string expression)
    {
        Assert.ThrowsAny<FormatException>(() => CronSchedule.Parse(expression));
    }

    [Fact]
    public void NextOccurrence_AdvancesByStep()
    {
        var schedule = CronSchedule.Parse("*/5 * * * *");
        var next = schedule.NextOccurrence(At(2026, 9, 15, 10, 4));
        Assert.Equal(At(2026, 9, 15, 10, 5), next);
    }

    [Fact]
    public void NextOccurrence_HonoursHourAndList()
    {
        var schedule = CronSchedule.Parse("30 2 1,15 * *");
        var next = schedule.NextOccurrence(At(2026, 9, 15, 10, 0));
        Assert.Equal(At(2026, 10, 1, 2, 30), next);
    }

    [Fact]
    public void NextOccurrence_HonoursDayOfWeek()
    {
        // 2026-09-15 is a Tuesday; next Monday noon is 2026-09-21.
        var schedule = CronSchedule.Parse("0 12 * * 1");
        var next = schedule.NextOccurrence(At(2026, 9, 15, 13, 0));
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(At(2026, 9, 21, 12, 0), next);
    }

    [Fact]
    public void NextOccurrence_IsStrictlyAfterFrom()
    {
        var schedule = CronSchedule.Parse("* * * * *");
        var from = At(2026, 9, 15, 10, 30);
        Assert.Equal(At(2026, 9, 15, 10, 31), schedule.NextOccurrence(from));
    }

    [Fact]
    public void NextOccurrence_ThrowsWhenItNeverMatches()
    {
        // February 31st never exists.
        var schedule = CronSchedule.Parse("0 0 31 2 *");
        Assert.Throws<CronFormatException>(() => schedule.NextOccurrence(At(2026, 1, 1, 0, 0)));
    }
}
