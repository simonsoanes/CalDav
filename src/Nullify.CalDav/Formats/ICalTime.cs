// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;

namespace Nullify.CalDav.Formats;

/// <summary>A DATE or DATE-TIME value as written in the iCalendar data.</summary>
public readonly record struct ICalDateTime(DateTime Value, bool IsDate, bool IsUtc, string? TzId)
{
    public bool IsFloating => !IsUtc && TzId == null;
}

public static class ICalTime
{
    public static bool TryParse(string text, string? tzId, out ICalDateTime result)
    {
        result = default;
        text = text.Trim();
        if (text.Length == 8 &&
            DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            result = new ICalDateTime(d, true, false, null);
            return true;
        }

        var utc = text.EndsWith('Z') || text.EndsWith('z');
        var core = utc ? text[..^1] : text;
        if (core.Length == 15 &&
            DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = utc
                ? new ICalDateTime(DateTime.SpecifyKind(dt, DateTimeKind.Utc), false, true, null)
                : new ICalDateTime(dt, false, false, string.IsNullOrEmpty(tzId) ? null : tzId);
            return true;
        }
        return false;
    }

    public static bool TryParse(ContentLine? line, out ICalDateTime result)
    {
        result = default;
        if (line == null) return false;
        var value = line.Value;
        var comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma];
        return TryParse(value, line.GetParameter("TZID"), out result);
    }

    /// <summary>Parses every value of a multi-valued property such as EXDATE or RDATE.</summary>
    public static IEnumerable<ICalDateTime> ParseList(ContentLine line)
    {
        var tz = line.GetParameter("TZID");
        foreach (var part in line.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = part;
            var slash = v.IndexOf('/');
            if (slash >= 0) v = v[..slash]; // PERIOD value: use start
            if (TryParse(v, tz, out var r)) yield return r;
        }
    }

    public static string FormatUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses an RFC 5545 DURATION value such as "-P1DT2H" or "PT15M".</summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToUpperInvariant();
        var i = 0;
        var negative = false;
        if (s[i] is '+' or '-') { negative = s[i] == '-'; i++; }
        if (i >= s.Length || s[i] != 'P') return false;
        i++;
        var inTime = false;
        long total = 0;
        var any = false;
        while (i < s.Length)
        {
            if (s[i] == 'T') { inTime = true; i++; continue; }
            var start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (start == i || i >= s.Length) return false;
            var n = long.Parse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            var unit = s[i++];
            total += unit switch
            {
                'W' when !inTime => n * 7 * 86400,
                'D' when !inTime => n * 86400,
                'H' when inTime => n * 3600,
                'M' when inTime => n * 60,
                'S' when inTime => n,
                _ => throw new FormatException("Invalid duration"),
            };
            any = true;
        }
        if (!any) return false;
        duration = TimeSpan.FromSeconds(negative ? -total : total);
        return true;
    }
}

/// <summary>Resolves TZID values to <see cref="TimeZoneInfo"/>, falling back to embedded VTIMEZONE definitions.</summary>
public sealed class TimeZoneResolver
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> SystemCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Component> _embedded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeZoneInfo> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public TimeZoneResolver(Component? vcalendar)
    {
        if (vcalendar == null) return;
        foreach (var tz in vcalendar.ChildrenNamed("VTIMEZONE"))
        {
            var id = tz.Value("TZID");
            if (!string.IsNullOrEmpty(id)) _embedded[id] = tz;
        }
    }

    public DateTime ToUtc(ICalDateTime value)
    {
        if (value.IsUtc) return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        if (value.IsDate || value.TzId == null) return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return ToUtc(value.Value, value.TzId);
    }

    /// <summary>Converts a wall-clock time in the given zone to UTC. Floating times are treated as UTC.</summary>
    public DateTime ToUtc(DateTime local, string? tzId)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = tzId == null ? null : Resolve(tzId);
        if (zone == null) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        try
        {
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        catch (ArgumentException)
        {
            return DateTime.SpecifyKind(local - zone.BaseUtcOffset, DateTimeKind.Utc);
        }
    }

    public TimeZoneInfo? Resolve(string tzId)
    {
        if (_resolved.TryGetValue(tzId, out var cached)) return cached;
        var zone = FindSystemZone(tzId);
        if (zone == null && _embedded.TryGetValue(tzId, out var vtz)) zone = FromVTimeZone(tzId, vtz);
        if (zone != null) _resolved[tzId] = zone;
        return zone;
    }

    public static TimeZoneInfo? FindSystemZone(string tzId) => SystemCache.GetOrAdd(tzId, static id =>
    {
        foreach (var candidate in Candidates(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var iana))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch { }
            }
        }
        return null;
    });

    private static IEnumerable<string> Candidates(string id)
    {
        var trimmed = id.Trim().Trim('"');
        yield return trimmed;
        // Mozilla style "/mozilla.org/20050126_1/Europe/London" or "/citadel.org/.../Europe/London"
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            yield return string.Join('/', parts[^2..]);
            if (parts.Length >= 3) yield return string.Join('/', parts[^3..]);
        }
    }

    /// <summary>
    /// Builds a zone from a VTIMEZONE definition, supporting the common case of yearly
    /// STANDARD/DAYLIGHT rules (BYMONTH + BYDAY). Anything else falls back to a fixed offset.
    /// </summary>
    private static TimeZoneInfo? FromVTimeZone(string id, Component vtz)
    {
        static Component? Latest(IEnumerable<Component> items) => items
            .Select(c => (c, ok: ICalTime.TryParse(c.Property("DTSTART"), out var d), d))
            .OrderByDescending(x => x.ok ? x.d.Value : DateTime.MinValue)
            .Select(x => x.c)
            .FirstOrDefault();

        var standard = Latest(vtz.ChildrenNamed("STANDARD"));
        var daylight = Latest(vtz.ChildrenNamed("DAYLIGHT"));
        if (standard == null && daylight == null) return null;

        if (!TryParseOffset((standard ?? daylight)!.Value("TZOFFSETTO"), out var baseOffset)) return null;

        try
        {
            if (standard != null && daylight != null &&
                TryParseOffset(daylight.Value("TZOFFSETTO"), out var dstOffset) &&
                TryTransition(daylight, out var startRule) &&
                TryTransition(standard, out var endRule))
            {
                var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date, DateTime.MaxValue.Date, dstOffset - baseOffset, startRule, endRule);
                return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id, id + " (DST)", [rule]);
            }
            return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id);
        }
    }

    private static bool TryTransition(Component c, out TimeZoneInfo.TransitionTime transition)
    {
        transition = default;
        if (!ICalTime.TryParse(c.Property("DTSTART"), out var start)) return false;
        var rrule = c.Value("RRULE");
        if (rrule == null || !RecurrenceRule.TryParse(rrule, out var rule) || rule.ByMonth.Count != 1 || rule.ByDay.Count != 1)
            return false;
        var (ordinal, dow) = rule.ByDay[0];
        var week = ordinal switch { < 0 => 5, 0 => 1, > 5 => 5, _ => ordinal };
        var timeOfDay = new DateTime(1, 1, 1, start.Value.Hour, start.Value.Minute, start.Value.Second);
        transition = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(timeOfDay, rule.ByMonth[0], week, dow);
        return true;
    }

    public static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Length < 5 || (text[0] != '+' && text[0] != '-')) return false;
        if (!int.TryParse(text.AsSpan(1, 2), out var h) || !int.TryParse(text.AsSpan(3, 2), out var m)) return false;
        var s = 0;
        if (text.Length >= 7) int.TryParse(text.AsSpan(5, 2), out s);
        offset = new TimeSpan(h, m, s);
        if (text[0] == '-') offset = -offset;
        return true;
    }
}
