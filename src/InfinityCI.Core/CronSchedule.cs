namespace InfinityCI.Core;

/// <summary>
/// Standard 5-field cron expression (minute hour day-of-month month day-of-week),
/// server-local time. Supports "*", numbers, ranges (a-b), lists (a,b,c),
/// steps (*&#47;n and a-b/n). Day-of-week accepts 0-7 (0 and 7 are Sunday).
/// Days-of-month and day-of-week combine with OR when both are restricted
/// (standard Vixie-cron behaviour).
/// </summary>
public sealed class CronSchedule
{
    private readonly int[] _minutes;
    private readonly int[] _hours;
    private readonly int[] _daysOfMonth;
    private readonly int[] _months;
    private readonly int[] _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronSchedule(int[] minutes, int[] hours, int[] daysOfMonth, int[] months, int[] daysOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = daysOfMonth.Length != 31;
        _dowRestricted = daysOfWeek.Length != 7;
    }

    public static CronSchedule Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
            throw new CronFormatException($"Cron expression '{expression}' must have 5 fields (minute hour day month weekday).");
        try
        {
            return new CronSchedule(
                ParseField(fields[0], 0, 59),
                ParseField(fields[1], 0, 23),
                ParseField(fields[2], 1, 31),
                ParseField(fields[3], 1, 12),
                NormalizeWeekdays(ParseField(fields[4], 0, 7)));
        }
        catch (CronFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CronFormatException($"Cron expression '{expression}' is invalid: {ex.Message}", ex);
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="from"/>, at second precision 0.
    /// Scans minute by minute (a valid expression always hits within a few years).</summary>
    public DateTimeOffset NextOccurrence(DateTimeOffset from)
    {
        // Round up to the next whole minute.
        var candidate = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, from.Offset);
        if (candidate <= from)
            candidate = candidate.AddMinutes(1);

        // Bounded scan: ~4 years of minutes covers every legal calendar combination.
        var limit = candidate.AddYears(4);
        while (candidate < limit)
        {
            if (_months.Contains(candidate.Month)
                && DayMatches(candidate)
                && _hours.Contains(candidate.Hour)
                && _minutes.Contains(candidate.Minute))
            {
                return candidate;
            }
            candidate = candidate.AddMinutes(1);
        }
        throw new CronFormatException("Cron expression never matches (day-of-month out of range for all months?).");
    }

    private bool DayMatches(DateTimeOffset time)
    {
        var dom = _daysOfMonth.Contains(time.Day);
        var dow = _daysOfWeek.Contains((int)time.DayOfWeek);
        if (_domRestricted && _dowRestricted)
            return dom || dow;
        if (_domRestricted)
            return dom;
        if (_dowRestricted)
            return dow;
        return true;
    }

    private static int[] ParseField(string field, int min, int max)
    {
        var values = new SortedSet<int>();
        foreach (var part in field.Split(','))
        {
            var (range, step) = SplitStep(part);
            var (start, end) = ParseRange(range, min, max);
            if (step < 1)
                throw new CronFormatException($"Invalid step in cron field '{field}'.");
            for (var v = start; v <= end; v += step)
                values.Add(v);
        }
        if (values.Count == 0)
            throw new CronFormatException($"Cron field '{field}' matches no values.");
        return values.ToArray();
    }

    private static (string Range, int Step) SplitStep(string part)
    {
        var slash = part.IndexOf('/');
        if (slash < 0)
            return (part, 1);
        return (part[..slash], int.Parse(part[(slash + 1)..]));
    }

    private static (int Start, int End) ParseRange(string range, int min, int max)
    {
        if (range == "*")
            return (min, max);
        var dash = range.IndexOf('-');
        if (dash < 0)
        {
            var single = int.Parse(range);
            if (single < min || single > max)
                throw new CronFormatException($"Cron value {single} is out of range [{min}, {max}].");
            return (single, single);
        }
        var start = int.Parse(range[..dash]);
        var end = int.Parse(range[(dash + 1)..]);
        if (start < min || end > max || start > end)
            throw new CronFormatException($"Cron range '{range}' is out of range [{min}, {max}].");
        return (start, end);
    }

    private static int[] NormalizeWeekdays(int[] days) =>
        days.Select(d => d == 7 ? 0 : d).Distinct().OrderBy(d => d).ToArray();
}

public sealed class CronFormatException(string message, Exception? inner = null)
    : FormatException(message, inner);
