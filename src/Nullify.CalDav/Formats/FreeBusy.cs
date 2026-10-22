using System.Text;

namespace Nullify.CalDav.Formats;

public static class FreeBusy
{
    public readonly record struct Period(DateTime Start, DateTime End, string Type);

    /// <summary>Busy periods of the VEVENTs in the given calendar objects within the range (RFC 4791 §7.10).</summary>
    public static List<Period> Compute(IEnumerable<string> objects, DateTime rangeStart, DateTime rangeEnd)
    {
        var busy = new List<Period>();
        foreach (var data in objects)
        {
            Component parsed;
            try { parsed = CalendarObjectInfo.ParseCalendar(data); }
            catch (InvalidObjectException) { continue; }
            var analyzer = new CalendarAnalyzer(parsed);
            foreach (var inst in analyzer.GetInstances("VEVENT", rangeStart, rangeEnd))
            {
                if (string.Equals(inst.Source.Value("TRANSP")?.Trim(), "TRANSPARENT", StringComparison.OrdinalIgnoreCase)) continue;
                var status = inst.Source.Value("STATUS")?.Trim().ToUpperInvariant();
                if (status == "CANCELLED" || inst.EndUtc <= inst.StartUtc) continue;
                var s = inst.StartUtc < rangeStart ? rangeStart : inst.StartUtc;
                var e = inst.EndUtc > rangeEnd ? rangeEnd : inst.EndUtc;
                busy.Add(new Period(s, e, status == "TENTATIVE" ? "BUSY-TENTATIVE" : "BUSY"));
            }
        }
        return busy.GroupBy(b => b.Type).SelectMany(g => Merge(g.Select(p => (p.Start, p.End))).Select(m => new Period(m.Item1, m.Item2, g.Key)))
            .OrderBy(p => p.Start).ToList();
    }

    private static IEnumerable<(DateTime, DateTime)> Merge(IEnumerable<(DateTime Start, DateTime End)> periods)
    {
        (DateTime Start, DateTime End)? current = null;
        foreach (var p in periods.OrderBy(p => p.Start))
        {
            if (current == null) current = p;
            else if (p.Start <= current.Value.End) current = (current.Value.Start, p.End > current.Value.End ? p.End : current.Value.End);
            else
            {
                yield return current.Value;
                current = p;
            }
        }
        if (current != null) yield return current.Value;
    }

    /// <summary>Writes a VFREEBUSY calendar. Extra lines (ORGANIZER, ATTENDEE, UID) can be supplied for scheduling replies.</summary>
    public static string Write(IEnumerable<Period> periods, DateTime rangeStart, DateTime rangeEnd, string? method = null, IEnumerable<string>? extraLines = null)
    {
        var sb = new StringBuilder();
        void Line(string l) => ContentWriter.AppendFolded(sb, l);
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Nullify//Nullify.CalDav//EN");
        Line("CALSCALE:GREGORIAN");
        if (method != null) Line("METHOD:" + method);
        Line("BEGIN:VFREEBUSY");
        Line("DTSTAMP:" + ICalTime.FormatUtc(DateTime.UtcNow));
        Line("DTSTART:" + ICalTime.FormatUtc(rangeStart));
        Line("DTEND:" + ICalTime.FormatUtc(rangeEnd));
        foreach (var l in extraLines ?? []) Line(l);
        foreach (var p in periods) Line($"FREEBUSY;FBTYPE={p.Type}:{ICalTime.FormatUtc(p.Start)}/{ICalTime.FormatUtc(p.End)}");
        Line("END:VFREEBUSY");
        Line("END:VCALENDAR");
        return sb.ToString();
    }
}
