namespace Nullify.CalDav.Formats;

/// <summary>A single (possibly recurring) instance of a component, in UTC.</summary>
public sealed record Instance(Component Source, DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Time-range evaluation for calendar object resources following RFC 4791 section 9.9,
/// including RRULE/RDATE/EXDATE expansion and RECURRENCE-ID overrides.
/// </summary>
public sealed class CalendarAnalyzer
{
    public const int MaxInstances = 20_000;

    public Component Root { get; }
    public TimeZoneResolver TimeZones { get; }

    public CalendarAnalyzer(Component vcalendar)
    {
        Root = vcalendar;
        TimeZones = new TimeZoneResolver(vcalendar);
    }

    public static bool Overlaps(DateTime start, DateTime end, DateTime rangeStart, DateTime rangeEnd) =>
        start == end
            ? rangeStart <= start && start < rangeEnd
            : start < rangeEnd && end > rangeStart;

    public DateTime ToUtc(ICalDateTime value) => TimeZones.ToUtc(value);

    private enum TimingKind { Normal, Always, Never }

    private readonly record struct Timing(TimingKind Kind, ICalDateTime Start, TimeSpan Duration, DateTime FixedStart, DateTime FixedEnd);

    private Timing GetTiming(Component c)
    {
        var hasStart = ICalTime.TryParse(c.Property("DTSTART"), out var start);
        switch (c.Name)
        {
            case "VEVENT":
            {
                if (!hasStart) return new Timing(TimingKind.Never, default, default, default, default);
                if (ICalTime.TryParse(c.Property("DTEND"), out var end))
                    return new Timing(TimingKind.Normal, start, Max0(ToUtc(end) - ToUtc(start)), default, default);
                if (ICalTime.TryParseDuration(c.Value("DURATION"), out var dur))
                    return new Timing(TimingKind.Normal, start, Max0(dur), default, default);
                return new Timing(TimingKind.Normal, start, start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero, default, default);
            }
            case "VTODO":
            {
                var hasDue = ICalTime.TryParse(c.Property("DUE"), out var due);
                if (hasStart)
                {
                    if (ICalTime.TryParseDuration(c.Value("DURATION"), out var dur))
                        return new Timing(TimingKind.Normal, start, Max0(dur), default, default);
                    if (hasDue)
                        return new Timing(TimingKind.Normal, start, Max0(ToUtc(due) - ToUtc(start)), default, default);
                    return new Timing(TimingKind.Normal, start, TimeSpan.Zero, default, default);
                }
                if (hasDue)
                    return new Timing(TimingKind.Normal, due, TimeSpan.Zero, default, default);

                var hasCompleted = ICalTime.TryParse(c.Property("COMPLETED"), out var completed);
                var hasCreated = ICalTime.TryParse(c.Property("CREATED"), out var created);
                if (hasCompleted && hasCreated)
                    return new Timing(TimingKind.Normal, default, default, ToUtc(created), ToUtc(completed));
                if (hasCompleted)
                {
                    var t = ToUtc(completed);
                    return new Timing(TimingKind.Normal, default, default, t, t);
                }
                if (hasCreated)
                    return new Timing(TimingKind.Normal, default, default, ToUtc(created), DateTime.MaxValue);
                return new Timing(TimingKind.Always, default, default, DateTime.MinValue, DateTime.MaxValue);
            }
            case "VJOURNAL":
            {
                if (!hasStart) return new Timing(TimingKind.Never, default, default, default, default);
                return new Timing(TimingKind.Normal, start, start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero, default, default);
            }
            case "VFREEBUSY":
            {
                if (hasStart && ICalTime.TryParse(c.Property("DTEND"), out var end))
                    return new Timing(TimingKind.Normal, start, Max0(ToUtc(end) - ToUtc(start)), default, default);
                return new Timing(TimingKind.Always, default, default, DateTime.MinValue, DateTime.MaxValue);
            }
            default:
                return new Timing(TimingKind.Always, default, default, DateTime.MinValue, DateTime.MaxValue);
        }
    }

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private Instance? BaseInstance(Component c, Timing timing)
    {
        if (timing.Kind == TimingKind.Never) return null;
        if (timing.Kind == TimingKind.Always || timing.Start.Value == default)
            return new Instance(c, timing.FixedStart, timing.FixedEnd);
        var s = ToUtc(timing.Start);
        return new Instance(c, s, SafeAdd(s, timing.Duration));
    }

    private static DateTime SafeAdd(DateTime t, TimeSpan d) =>
        DateTime.MaxValue - t < d ? DateTime.MaxValue : t + d;

    /// <summary>Returns all instances of components named <paramref name="componentName"/> overlapping the range.</summary>
    public List<Instance> GetInstances(string componentName, DateTime rangeStart, DateTime rangeEnd)
    {
        var result = new List<Instance>();
        var comps = Root.ChildrenNamed(componentName).ToList();
        if (comps.Count == 0) return result;

        var overrides = comps.Where(c => c.Property("RECURRENCE-ID") != null).ToList();
        var overrideIds = new HashSet<DateTime>();
        foreach (var o in overrides)
        {
            if (ICalTime.TryParse(o.Property("RECURRENCE-ID"), out var rid)) overrideIds.Add(ToUtc(rid));
            var inst = BaseInstance(o, GetTiming(o));
            if (inst != null && Overlaps(inst.StartUtc, inst.EndUtc, rangeStart, rangeEnd)) result.Add(inst);
        }

        foreach (var master in comps.Where(c => c.Property("RECURRENCE-ID") == null))
            ExpandMaster(master, rangeStart, rangeEnd, overrideIds, result);

        return result;
    }

    private void ExpandMaster(Component master, DateTime rangeStart, DateTime rangeEnd, HashSet<DateTime> overrideIds, List<Instance> result)
    {
        var timing = GetTiming(master);
        var rrules = master.PropertiesNamed("RRULE").ToList();
        var rdates = master.PropertiesNamed("RDATE").ToList();

        if (timing.Kind != TimingKind.Normal || timing.Start.Value == default || (rrules.Count == 0 && rdates.Count == 0))
        {
            var inst = BaseInstance(master, timing);
            if (inst != null && Overlaps(inst.StartUtc, inst.EndUtc, rangeStart, rangeEnd)) result.Add(inst);
            return;
        }

        var start = timing.Start;
        var exdates = new HashSet<DateTime>();
        foreach (var ex in master.PropertiesNamed("EXDATE"))
        foreach (var v in ICalTime.ParseList(ex))
            exdates.Add(ToUtc(v));

        var seen = new HashSet<DateTime>();
        var produced = 0;

        void Add(DateTime local, ICalDateTime basis)
        {
            var sUtc = basis.IsUtc ? DateTime.SpecifyKind(local, DateTimeKind.Utc) : TimeZones.ToUtc(local, basis.IsDate ? null : basis.TzId);
            if (!seen.Add(sUtc) || exdates.Contains(sUtc) || overrideIds.Contains(sUtc)) return;
            DateTime eUtc;
            if (basis.IsDate || basis.TzId == null || basis.IsUtc) eUtc = SafeAdd(sUtc, timing.Duration);
            else eUtc = TimeZones.ToUtc(local + timing.Duration, basis.TzId);
            if (Overlaps(sUtc, eUtc, rangeStart, rangeEnd)) result.Add(new Instance(master, sUtc, eUtc));
        }

        foreach (var rr in rrules)
        {
            if (!RecurrenceRule.TryParse(rr.Value, out var rule))
            {
                Add(start.Value, start);
                continue;
            }

            var isPastUntil = BuildUntil(rule, start);
            foreach (var occ in rule.Enumerate(start.Value, isPastUntil))
            {
                var sUtc = start.IsUtc ? occ : TimeZones.ToUtc(occ, start.IsDate ? null : start.TzId);
                if (sUtc >= rangeEnd) break;
                // Skip instances that end long before the range without paying for conversion of the end time.
                if (SafeAdd(sUtc, timing.Duration + TimeSpan.FromDays(1)) < rangeStart) { if (++produced > MaxInstances * 10) break; continue; }
                Add(occ, start);
                if (++produced > MaxInstances * 10 || result.Count >= MaxInstances) break;
            }
        }

        if (rrules.Count == 0) Add(start.Value, start);

        foreach (var rd in rdates)
        foreach (var v in ICalTime.ParseList(rd))
            Add(v.Value, v.TzId == null && !v.IsUtc && !v.IsDate && start.TzId != null ? v with { TzId = start.TzId } : v);
    }

    private Func<DateTime, bool>? BuildUntil(RecurrenceRule rule, ICalDateTime start)
    {
        if (rule.Until == null || !ICalTime.TryParse(rule.Until, null, out var until)) return null;
        if (until.IsDate)
        {
            var lastDay = until.Value.Date;
            return t => t.Date > lastDay;
        }
        if (until.IsUtc && !start.IsUtc && start.TzId != null && !start.IsDate)
        {
            var u = until.Value;
            return t => TimeZones.ToUtc(t, start.TzId) > u;
        }
        var limit = until.Value;
        return t => t > limit;
    }

    /// <summary>
    /// Computes the overall [first, last] instant span of the object for database pre-filtering.
    /// Null means unbounded on that side.
    /// </summary>
    public (DateTime? First, DateTime? Last) GetBounds()
    {
        DateTime? first = null;
        DateTime? last = null;
        var unboundedStart = false;
        var unboundedEnd = false;

        foreach (var c in Root.Children)
        {
            if (c.Name is not ("VEVENT" or "VTODO" or "VJOURNAL" or "VFREEBUSY")) continue;
            var timing = GetTiming(c);
            if (timing.Kind == TimingKind.Never) continue;
            if (timing.Kind == TimingKind.Always) { unboundedStart = unboundedEnd = true; continue; }

            var inst = BaseInstance(c, timing)!;
            if (inst.StartUtc == DateTime.MinValue) unboundedStart = true;
            if (inst.EndUtc == DateTime.MaxValue) unboundedEnd = true;
            first = first == null || inst.StartUtc < first ? inst.StartUtc : first;
            last = last == null || inst.EndUtc > last ? inst.EndUtc : last;

            if (c.Property("RECURRENCE-ID") != null) continue;
            var rrules = c.PropertiesNamed("RRULE").ToList();
            if (rrules.Count > 0)
            {
                foreach (var rr in rrules)
                {
                    if (!RecurrenceRule.TryParse(rr.Value, out var rule) || !rule.IsBounded) { unboundedEnd = true; break; }
                }
                if (!unboundedEnd)
                {
                    var all = GetInstances(c.Name, DateTime.MinValue, DateTime.MaxValue);
                    if (all.Count >= MaxInstances) unboundedEnd = true;
                    foreach (var i in all)
                    {
                        if (i.StartUtc < first) first = i.StartUtc;
                        if (i.EndUtc > last) last = i.EndUtc;
                    }
                }
            }
            foreach (var rd in c.PropertiesNamed("RDATE"))
            foreach (var v in ICalTime.ParseList(rd))
            {
                var s = ToUtc(v);
                if (s < first) first = s;
                var e = SafeAdd(s, timing.Duration);
                if (e > last) last = e;
            }
        }

        return (unboundedStart ? null : first, unboundedEnd ? null : last);
    }
}
