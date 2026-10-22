using System.Globalization;

namespace Nullify.CalDav.Formats;

public enum Frequency { Secondly, Minutely, Hourly, Daily, Weekly, Monthly, Yearly }

/// <summary>Parsed RRULE (RFC 5545 3.3.10).</summary>
public sealed class RecurrenceRule
{
    public Frequency Freq { get; set; }
    public int Interval { get; set; } = 1;
    public int? Count { get; set; }
    public string? Until { get; set; }
    public List<int> BySecond { get; } = [];
    public List<int> ByMinute { get; } = [];
    public List<int> ByHour { get; } = [];
    public List<(int Ordinal, DayOfWeek Day)> ByDay { get; } = [];
    public List<int> ByMonthDay { get; } = [];
    public List<int> ByYearDay { get; } = [];
    public List<int> ByWeekNo { get; } = [];
    public List<int> ByMonth { get; } = [];
    public List<int> BySetPos { get; } = [];
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    public bool IsBounded => Count.HasValue || Until != null;

    public static bool TryParse(string text, out RecurrenceRule rule)
    {
        rule = new RecurrenceRule();
        var hasFreq = false;
        try
        {
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim().ToUpperInvariant();
                var value = part[(eq + 1)..].Trim();
                switch (key)
                {
                    case "FREQ":
                        if (!Enum.TryParse(value, true, out Frequency f)) return false;
                        rule.Freq = f;
                        hasFreq = true;
                        break;
                    case "INTERVAL": rule.Interval = Math.Max(1, int.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "COUNT": rule.Count = Math.Max(0, int.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "UNTIL": rule.Until = value; break;
                    case "BYSECOND": AddInts(rule.BySecond, value); break;
                    case "BYMINUTE": AddInts(rule.ByMinute, value); break;
                    case "BYHOUR": AddInts(rule.ByHour, value); break;
                    case "BYMONTHDAY": AddInts(rule.ByMonthDay, value); break;
                    case "BYYEARDAY": AddInts(rule.ByYearDay, value); break;
                    case "BYWEEKNO": AddInts(rule.ByWeekNo, value); break;
                    case "BYMONTH": AddInts(rule.ByMonth, value); break;
                    case "BYSETPOS": AddInts(rule.BySetPos, value); break;
                    case "WKST": if (TryParseDay(value, out var wk)) rule.WeekStart = wk; break;
                    case "BYDAY":
                        foreach (var d in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (d.Length < 2 || !TryParseDay(d[^2..], out var dow)) return false;
                            var ord = d.Length > 2 ? int.Parse(d[..^2], CultureInfo.InvariantCulture) : 0;
                            rule.ByDay.Add((ord, dow));
                        }
                        break;
                }
            }
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        return hasFreq;
    }

    private static void AddInts(List<int> list, string value)
    {
        foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(int.Parse(v, CultureInfo.InvariantCulture));
    }

    private static bool TryParseDay(string s, out DayOfWeek day)
    {
        day = s.ToUpperInvariant() switch
        {
            "SU" => DayOfWeek.Sunday,
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1),
        };
        return (int)day >= 0;
    }

    /// <summary>
    /// Enumerates occurrence start times (in the same wall-clock basis as <paramref name="start"/>),
    /// beginning with DTSTART itself. <paramref name="isPastUntil"/> evaluates the UNTIL bound.
    /// </summary>
    public IEnumerable<DateTime> Enumerate(DateTime start, Func<DateTime, bool>? isPastUntil = null, int maxPeriods = 50_000)
    {
        isPastUntil ??= static _ => false;
        if (Count == 0) yield break;
        if (isPastUntil(start)) yield break;

        yield return start;
        var emitted = 1;
        if (Count == 1) yield break;

        var byMonth = ByMonth;
        var byMonthDay = ByMonthDay;
        var byDay = ByDay;
        var noDayRules = ByWeekNo.Count == 0 && ByYearDay.Count == 0 && ByMonthDay.Count == 0 && ByDay.Count == 0;
        if (noDayRules)
        {
            switch (Freq)
            {
                case Frequency.Yearly:
                    if (byMonth.Count == 0) byMonth = [start.Month];
                    byMonthDay = [start.Day];
                    break;
                case Frequency.Monthly:
                    byMonthDay = [start.Day];
                    break;
                case Frequency.Weekly:
                    byDay = [(0, start.DayOfWeek)];
                    break;
            }
        }

        var hours = ByHour.Count > 0 ? ByHour.Order().ToList() : [start.Hour];
        var minutes = ByMinute.Count > 0 ? ByMinute.Order().ToList() : [start.Minute];
        var seconds = BySecond.Count > 0 ? BySecond.Order().ToList() : [start.Second];
        var interval = Math.Max(1, Interval);
        var explicitMonth = ByMonth.Count > 0;

        bool DayMatches(DateTime day)
        {
            if (byMonth.Count > 0 && !byMonth.Contains(day.Month)) return false;
            if (ByWeekNo.Count > 0 && Freq == Frequency.Yearly && !WeekNoMatches(day)) return false;
            if (ByYearDay.Count > 0)
            {
                var diy = DateTime.IsLeapYear(day.Year) ? 366 : 365;
                if (!ByYearDay.Any(n => n == day.DayOfYear || n == day.DayOfYear - diy - 1)) return false;
            }
            if (byMonthDay.Count > 0)
            {
                var dim = DateTime.DaysInMonth(day.Year, day.Month);
                if (!byMonthDay.Any(n => n == day.Day || n == day.Day - dim - 1)) return false;
            }
            if (byDay.Count > 0 && !byDay.Any(bd => bd.Day == day.DayOfWeek && OrdinalMatches(bd.Ordinal, day)))
                return false;
            return true;
        }

        bool OrdinalMatches(int n, DateTime day)
        {
            if (n == 0) return true;
            if (Freq == Frequency.Monthly || (Freq == Frequency.Yearly && explicitMonth))
            {
                var dim = DateTime.DaysInMonth(day.Year, day.Month);
                return n > 0 ? (day.Day - 1) / 7 + 1 == n : (dim - day.Day) / 7 + 1 == -n;
            }
            if (Freq == Frequency.Yearly && ByWeekNo.Count == 0)
            {
                var diy = DateTime.IsLeapYear(day.Year) ? 366 : 365;
                return n > 0 ? (day.DayOfYear - 1) / 7 + 1 == n : (diy - day.DayOfYear) / 7 + 1 == -n;
            }
            return true;
        }

        bool WeekNoMatches(DateTime day)
        {
            var (week, weeksInYear) = WeekNumber(day, WeekStart);
            return ByWeekNo.Any(n => n == week || n == week - weeksInYear - 1);
        }

        var candidates = new List<DateTime>();
        var weekAnchor = start.Date.AddDays(-(((int)start.DayOfWeek - (int)WeekStart + 7) % 7));
        var monthAnchor = start.Year * 12L + start.Month - 1;

        for (long p = 0; p < maxPeriods; p++)
        {
            candidates.Clear();
            DateTime periodStart;
            try
            {
                switch (Freq)
                {
                    case Frequency.Yearly:
                    {
                        var year = start.Year + p * interval;
                        if (year > 9999) yield break;
                        periodStart = new DateTime((int)year, 1, 1);
                        foreach (var month in byMonth.Count > 0 ? byMonth.Where(m => m is >= 1 and <= 12).Order() : Enumerable.Range(1, 12))
                        {
                            var dim = DateTime.DaysInMonth((int)year, month);
                            for (var d = 1; d <= dim; d++)
                            {
                                var day = new DateTime((int)year, month, d);
                                if (DayMatches(day)) AddTimes(candidates, day, hours, minutes, seconds);
                            }
                        }
                        break;
                    }
                    case Frequency.Monthly:
                    {
                        var idx = monthAnchor + p * interval;
                        if (idx / 12 > 9999) yield break;
                        periodStart = new DateTime((int)(idx / 12), (int)(idx % 12) + 1, 1);
                        var dim = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);
                        for (var d = 0; d < dim; d++)
                        {
                            var day = periodStart.AddDays(d);
                            if (DayMatches(day)) AddTimes(candidates, day, hours, minutes, seconds);
                        }
                        break;
                    }
                    case Frequency.Weekly:
                    {
                        periodStart = weekAnchor.AddDays(7 * p * interval);
                        for (var d = 0; d < 7; d++)
                        {
                            var day = periodStart.AddDays(d);
                            if (DayMatches(day)) AddTimes(candidates, day, hours, minutes, seconds);
                        }
                        break;
                    }
                    case Frequency.Daily:
                    {
                        periodStart = start.Date.AddDays(p * interval);
                        if (DayMatches(periodStart)) AddTimes(candidates, periodStart, hours, minutes, seconds);
                        break;
                    }
                    case Frequency.Hourly:
                    {
                        periodStart = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0).AddHours(p * interval);
                        if (DayMatches(periodStart.Date) && (ByHour.Count == 0 || ByHour.Contains(periodStart.Hour)))
                            foreach (var m in minutes)
                            foreach (var s in seconds)
                                candidates.Add(periodStart.AddMinutes(m).AddSeconds(s));
                        break;
                    }
                    case Frequency.Minutely:
                    {
                        periodStart = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0).AddMinutes(p * interval);
                        if (DayMatches(periodStart.Date) && (ByHour.Count == 0 || ByHour.Contains(periodStart.Hour)) &&
                            (ByMinute.Count == 0 || ByMinute.Contains(periodStart.Minute)))
                            foreach (var s in seconds)
                                candidates.Add(periodStart.AddSeconds(s));
                        break;
                    }
                    default:
                    {
                        periodStart = start.AddSeconds(p * interval);
                        if (DayMatches(periodStart.Date) && (ByHour.Count == 0 || ByHour.Contains(periodStart.Hour)) &&
                            (ByMinute.Count == 0 || ByMinute.Contains(periodStart.Minute)) &&
                            (BySecond.Count == 0 || BySecond.Contains(periodStart.Second)))
                            candidates.Add(periodStart);
                        break;
                    }
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }

            if (p > 0 && isPastUntil(periodStart)) yield break;
            if (candidates.Count == 0) continue;

            candidates.Sort();
            IEnumerable<DateTime> selected = candidates;
            if (BySetPos.Count > 0)
            {
                var n = candidates.Count;
                selected = BySetPos
                    .Select(pos => pos > 0 ? pos - 1 : n + pos)
                    .Where(i => i >= 0 && i < n)
                    .Distinct()
                    .Order()
                    .Select(i => candidates[i])
                    .ToList();
            }

            foreach (var t in selected)
            {
                if (t <= start) continue;
                if (isPastUntil(t)) yield break;
                yield return t;
                emitted++;
                if (Count.HasValue && emitted >= Count.Value) yield break;
            }
        }
    }

    private static void AddTimes(List<DateTime> list, DateTime day, List<int> hours, List<int> minutes, List<int> seconds)
    {
        foreach (var h in hours)
        foreach (var m in minutes)
        foreach (var s in seconds)
        {
            if (h is < 0 or > 23 || m is < 0 or > 59 || s is < 0 or > 60) continue;
            list.Add(day.AddHours(h).AddMinutes(m).AddSeconds(Math.Min(s, 59)));
        }
    }

    /// <summary>RFC 5545 week number: week 1 is the first week containing at least four days of the year.</summary>
    internal static (int Week, int WeeksInYear) WeekNumber(DateTime day, DayOfWeek weekStart)
    {
        static DateTime Week1Start(int year, DayOfWeek ws)
        {
            var jan1 = new DateTime(year, 1, 1);
            var offset = ((int)jan1.DayOfWeek - (int)ws + 7) % 7;
            return offset <= 3 ? jan1.AddDays(-offset) : jan1.AddDays(7 - offset);
        }

        var year = day.Year;
        var w1 = Week1Start(year, weekStart);
        if (day < w1)
        {
            year--;
            w1 = Week1Start(year, weekStart);
        }
        else
        {
            var next = Week1Start(year + 1, weekStart);
            if (day >= next)
            {
                year++;
                w1 = next;
            }
        }
        var weeks = (Week1Start(year + 1, weekStart) - w1).Days / 7;
        return ((day - w1).Days / 7 + 1, weeks);
    }
}
