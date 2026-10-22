using System.Globalization;
using System.Text;

namespace Nullify.CalDav.Formats;

/// <summary>
/// Conversion between the formats stored by the server (vCard 3.0/4.0, iCalendar 2.0) and the
/// legacy formats used by many SyncML devices (vCard 2.1, vCalendar 1.0, vNote 1.1, plain text).
/// </summary>
public static class LegacyFormats
{
    // ------------------------------------------------------------------ Shared helpers

    /// <summary>Parses legacy content, joining quoted-printable soft line breaks first.</summary>
    public static List<Component> ParseLegacy(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            var isQp = colon > 0 && line[..colon].Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            while (isQp && line.EndsWith('=') && i + 1 < lines.Length)
                line = line[..^1] + lines[++i];
            sb.Append(line).Append("\r\n");
        }
        return ContentReader.Parse(sb.ToString());
    }

    private static bool IsQuotedPrintable(ContentLine p) =>
        p.Parameters.Any(x => (x.Name == "ENCODING" && x.Values.Any(v => v.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)))
                              || x.Name == "QUOTED-PRINTABLE");

    private static Encoding CharsetOf(ContentLine p)
    {
        var name = p.GetParameter("CHARSET");
        if (string.IsNullOrEmpty(name)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    /// <summary>Returns the raw (unescaped-by-encoding) value of a legacy property, decoding quoted-printable.</summary>
    private static string DecodedValue(ContentLine p) => IsQuotedPrintable(p) ? DecodeQp(p.Value, CharsetOf(p)) : p.Value;

    public static string DecodeQp(string value, Encoding encoding)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '=' && IsHex(value, i + 1))
            {
                bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else if (c == '=' && i == value.Length - 1)
            {
                // trailing soft break
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) bytes.Add(b);
            }
        }
        return encoding.GetString(bytes.ToArray());
    }

    private static bool IsHex(string s, int i) =>
        i + 1 < s.Length && Uri.IsHexDigit(s[i]) && Uri.IsHexDigit(s[i + 1]);

    public static string EncodeQp(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n").Replace("\n", "\r\n")))
        {
            if (b is >= 33 and <= 126 && b != '=' || b == ' ') sb.Append((char)b);
            else sb.Append('=').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static bool NeedsQp(string value) => value.Any(c => c > 126 || c == '\n' || c == '\r');

    /// <summary>Writes a legacy content line, using QP soft line breaks for QP values.</summary>
    private static void AppendLegacy(StringBuilder sb, ContentLine line)
    {
        var text = ContentWriter.Format(line);
        if (!IsQuotedPrintable(line))
        {
            ContentWriter.AppendFolded(sb, text);
            return;
        }
        var lineLength = 0;
        for (var i = 0; i < text.Length; i++)
        {
            // Never split inside an "=XX" escape.
            var chunk = text[i] == '=' && i + 2 < text.Length ? text.Substring(i, 3) : text[i].ToString();
            if (lineLength + chunk.Length > 74)
            {
                sb.Append("=\r\n");
                lineLength = 0;
            }
            sb.Append(chunk);
            lineLength += chunk.Length;
            i += chunk.Length - 1;
        }
        sb.Append("\r\n");
    }

    private static string WriteLegacy(Component c)
    {
        var sb = new StringBuilder();
        void Write(Component comp)
        {
            sb.Append("BEGIN:").Append(comp.Name).Append("\r\n");
            foreach (var p in comp.Properties) AppendLegacy(sb, p);
            foreach (var child in comp.Children) Write(child);
            sb.Append("END:").Append(comp.Name).Append("\r\n");
        }
        Write(c);
        return sb.ToString();
    }

    /// <summary>Sets a TEXT value on a legacy line, switching to QP + UTF-8 when required.</summary>
    private static ContentLine LegacyText(string name, string value, IEnumerable<ContentParameter>? extra = null)
    {
        var line = new ContentLine(name, value);
        if (extra != null) line.Parameters.AddRange(extra);
        if (NeedsQp(value))
        {
            line.Parameters.Add(new ContentParameter("CHARSET", ["UTF-8"]));
            line.Parameters.Add(new ContentParameter("ENCODING", ["QUOTED-PRINTABLE"]));
            line.Value = EncodeQp(value);
        }
        return line;
    }


    private static string EscapeText(string value) =>
        value.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\r\n", "\n").Replace("\n", "\\n");

    // ------------------------------------------------------------------ vCard

    private static readonly HashSet<string> TextProps = ["FN", "NOTE", "TITLE", "ROLE", "LABEL", "NICKNAME"];
    private static readonly HashSet<string> StructuredProps = ["N", "ADR", "ORG"];

    private static readonly HashSet<string> KnownTypes =
    [
        "HOME", "WORK", "CELL", "VOICE", "FAX", "PAGER", "MSG", "PREF", "INTERNET", "X400", "DOM", "INTL", "POSTAL",
        "PARCEL", "BBS", "MODEM", "CAR", "ISDN", "VIDEO", "JPEG", "GIF", "PNG", "BMP", "TIFF", "WAVE", "AIFF", "PCM",
        "MAIN", "OTHER", "IPHONE", "TEXT",
    ];

    /// <summary>Converts a vCard 2.1 (or 3.0) object to vCard 3.0 for storage.</summary>
    public static string VCardTo30(string input)
    {
        var roots = ParseLegacy(input);
        var src = roots.FirstOrDefault(r => r.Name == "VCARD") ?? throw new InvalidObjectException("Expected a VCARD");
        if (src.Value("VERSION")?.Trim() is "3.0" or "4.0") return ContentWriter.Write(src);

        var card = new Component("VCARD");
        card.Properties.Add(new ContentLine("VERSION", "3.0"));
        foreach (var p in src.Properties)
        {
            if (p.Name == "VERSION") continue;
            var value = DecodedValue(p);
            var line = new ContentLine { Name = p.Name, Group = p.Group };
            var types = new List<string>();
            foreach (var param in p.Parameters)
            {
                if (param.Name is "CHARSET") continue;
                if (param.Name == "ENCODING")
                {
                    if (param.Values.Any(v => v.Equals("BASE64", StringComparison.OrdinalIgnoreCase) || v.Equals("B", StringComparison.OrdinalIgnoreCase)))
                        line.Parameters.Add(new ContentParameter("ENCODING", ["b"]));
                    continue;
                }
                if (param.Name == "QUOTED-PRINTABLE") continue;
                if (param.Name == "BASE64") { line.Parameters.Add(new ContentParameter("ENCODING", ["b"])); continue; }
                if (param.Values.Count == 0 && KnownTypes.Contains(param.Name)) { types.Add(param.Name); continue; }
                if (param.Name == "TYPE") { types.AddRange(param.Values); continue; }
                line.Parameters.Add(param);
            }
            if (types.Count > 0) line.Parameters.Insert(0, new ContentParameter("TYPE", types.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

            if (TextProps.Contains(p.Name)) value = EscapeText(value);
            else if (StructuredProps.Contains(p.Name))
                value = string.Join(';', value.Split(';').Select(part => part.Replace("\\", "\\\\").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n")));
            else if (p.Name is "PHOTO" or "LOGO" or "SOUND" or "KEY") value = value.Replace(" ", "").Replace("\t", "");
            line.Value = value;
            card.Properties.Add(line);
        }

        if (card.Property("N") == null)
            card.Properties.Add(new ContentLine("N", EscapeText(card.Property("FN")?.TextValue ?? "") + ";;;;"));
        if (card.Property("FN") == null)
        {
            var n = card.Property("N")!.Value.Split(';');
            var fn = string.Join(' ', new[] { n.ElementAtOrDefault(3), n.ElementAtOrDefault(1), n.ElementAtOrDefault(2), n.ElementAtOrDefault(0), n.ElementAtOrDefault(4) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (fn.Length == 0) fn = card.Property("ORG")?.Value.Split(';')[0] ?? card.Property("EMAIL")?.Value ?? card.Property("TEL")?.Value ?? "Unnamed";
            card.Properties.Insert(1, new ContentLine("FN", fn));
        }
        return ContentWriter.Write(card);
    }

    /// <summary>Converts a stored vCard (3.0/4.0) to vCard 2.1.</summary>
    public static string VCardTo21(string stored)
    {
        var src = ContentReader.Parse(stored).First(r => r.Name == "VCARD");
        var card = new Component("VCARD");
        card.Properties.Add(new ContentLine("VERSION", "2.1"));
        foreach (var p in src.Properties)
        {
            if (p.Name is "VERSION" or "PRODID" or "KIND" or "SOURCE" or "CLIENTPIDMAP") continue;
            var extra = new List<ContentParameter>();
            var value = p.Value;
            var isBinary = false;
            foreach (var param in p.Parameters)
            {
                switch (param.Name)
                {
                    case "TYPE":
                        foreach (var t in param.Values.SelectMany(v => v.Split(',')))
                            extra.Add(new ContentParameter(t.Trim().ToUpperInvariant(), []));
                        break;
                    case "ENCODING":
                        isBinary = true;
                        extra.Add(new ContentParameter("ENCODING", ["BASE64"]));
                        break;
                    case "VALUE" or "PREF" or "PID" or "ALTID" or "MEDIATYPE" or "CALSCALE" or "SORT-AS" or "LABEL" or "GEO" or "TZ":
                        if (param.Name == "PREF") extra.Add(new ContentParameter("PREF", []));
                        break;
                    default:
                        extra.Add(param);
                        break;
                }
            }

            // vCard 4.0 URIs: tel: and data: values.
            if (value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && value.Contains(";base64,"))
            {
                var mime = value[5..value.IndexOf(';')];
                value = value[(value.IndexOf(',') + 1)..];
                extra.Add(new ContentParameter("ENCODING", ["BASE64"]));
                extra.Add(new ContentParameter(mime.Split('/').Last().ToUpperInvariant(), []));
                isBinary = true;
            }

            if (isBinary)
            {
                var bin = new ContentLine(p.Name, value) { Group = p.Group };
                bin.Parameters.AddRange(extra);
                card.Properties.Add(bin);
                continue;
            }

            // vCard 2.1 has no TEXT escaping; structured values keep their ';' separators.
            var text = StructuredProps.Contains(p.Name)
                ? string.Join(';', SplitUnescaped(value, ';').Select(ContentLine.UnescapeText))
                : ContentLine.UnescapeText(value);
            var line = LegacyText(p.Name, text, extra);
            line.Group = p.Group;
            card.Properties.Add(line);
        }
        return WriteLegacy(card);
    }

    private static IEnumerable<string> SplitUnescaped(string value, char separator)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[i]).Append(value[++i]);
                continue;
            }
            if (value[i] == separator)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else sb.Append(value[i]);
        }
        yield return sb.ToString();
    }

    // ------------------------------------------------------------------ vCalendar 1.0

    private static readonly string[] DateProps = ["DTSTART", "DTEND", "DUE", "COMPLETED", "EXDATE", "RDATE", "LAST-MODIFIED", "CREATED", "DCREATED", "RECURRENCE-ID"];
    private static readonly HashSet<string> CalTextProps = ["SUMMARY", "DESCRIPTION", "LOCATION", "COMMENT", "URL", "CLASS"];

    /// <summary>
    /// Converts a vCalendar 1.0 (or iCalendar 2.0) object to iCalendar 2.0 for storage. When updating an
    /// existing resource, the stored UID and properties vCalendar 1.0 cannot express are preserved.
    /// </summary>
    public static string VCalendarTo20(string input, string? existing, out string componentType)
    {
        var roots = ParseLegacy(input);
        var src = roots.FirstOrDefault(r => r.Name == "VCALENDAR") ?? throw new InvalidObjectException("Expected a VCALENDAR");
        var item = src.Children.FirstOrDefault(c => c.Name is "VEVENT" or "VTODO" or "VJOURNAL")
                   ?? throw new InvalidObjectException("No VEVENT or VTODO found");
        componentType = item.Name;

        Component? old = null;
        if (existing != null)
        {
            try { old = CalendarObjectInfo.ParseCalendar(existing).Children.FirstOrDefault(c => c.Name == item.Name && c.Property("RECURRENCE-ID") == null); }
            catch (InvalidObjectException) { old = null; }
        }

        if (src.Value("VERSION")?.Trim() == "2.0")
        {
            if (old?.Value("UID") is { } keepUid && item.Value("UID") != keepUid)
            {
                foreach (var c in src.Children.Where(c => CalendarObjectInfo.ObjectComponents.Contains(c.Name)))
                {
                    c.Properties.RemoveAll(p => p.Name == "UID");
                    c.Properties.Insert(0, new ContentLine("UID", keepUid));
                }
            }
            EnsureUid(src);
            return ContentWriter.Write(src);
        }

        TimeSpan? tzOffset = TimeZoneResolver.TryParseOffset(NormalizeOffset(src.Value("TZ")), out var off) ? off : null;
        var comp = new Component(item.Name);
        var uid = old?.Value("UID") ?? item.Value("UID")?.Trim();
        comp.Properties.Add(new ContentLine("UID", string.IsNullOrEmpty(uid) ? Guid.NewGuid().ToString() : uid));
        comp.Properties.Add(new ContentLine("DTSTAMP", ICalTime.FormatUtc(DateTime.UtcNow)));

        // All-day detection (vCalendar 1.0 has no DATE values).
        var allDay = false;
        if (item.Name == "VEVENT" && TryLocal(item.Property("DTSTART"), out var ls, out var lsUtc) && ls.TimeOfDay == TimeSpan.Zero && !lsUtc)
        {
            if (!TryLocal(item.Property("DTEND"), out var le, out _)) allDay = item.Value("DTSTART")!.Trim().Length == 8;
            else allDay = (le.TimeOfDay == TimeSpan.Zero && le > ls) || (le.Hour == 23 && le.Minute == 59);
            if (item.Value("DTSTART")!.Trim().Length == 8) allDay = true;
            if (allDay)
            {
                var endDate = TryLocal(item.Property("DTEND"), out var e2, out _)
                    ? (e2.TimeOfDay == TimeSpan.Zero && e2 > ls ? e2.Date : e2.Date.AddDays(1))
                    : ls.Date.AddDays(1);
                comp.Properties.Add(DateLine("DTSTART", ls.Date));
                comp.Properties.Add(DateLine("DTEND", endDate));
            }
        }

        DateTime? dtstartUtc = null;
        foreach (var p in item.Properties)
        {
            var value = DecodedValue(p).Trim();
            switch (p.Name)
            {
                case "UID" or "DTSTAMP" or "AALARM" or "DALARM" or "PALARM" or "MALARM" or "ATTENDEE" or "RRULE" or "ORGANIZER":
                    break;
                case "DTSTART" or "DTEND" when allDay:
                    break;
                case "DCREATED":
                    comp.Properties.Add(new ContentLine("CREATED", ConvertTime(value, tzOffset)));
                    break;
                case var n when DateProps.Contains(n):
                {
                    var converted = string.Join(',', value.Split(';', ',').Where(v => v.Length > 0).Select(v => ConvertTime(v, tzOffset)));
                    var line = new ContentLine(n, converted);
                    if (converted.Length == 8) line.Parameters.Add(new ContentParameter("VALUE", ["DATE"]));
                    comp.Properties.Add(line);
                    if (n == "DTSTART" && ICalTime.TryParse(converted, null, out var dts) && dts.IsUtc) dtstartUtc = dts.Value;
                    break;
                }
                case "STATUS":
                    if (MapStatusTo20(item.Name, value) is { } status) comp.Properties.Add(new ContentLine("STATUS", status));
                    break;
                case "TRANSP":
                    comp.Properties.Add(new ContentLine("TRANSP", value is "1" or "TRANSPARENT" ? "TRANSPARENT" : "OPAQUE"));
                    break;
                case "CATEGORIES":
                    comp.Properties.Add(new ContentLine("CATEGORIES", string.Join(',', value.Split(';', ',').Select(v => EscapeText(v.Trim())).Where(v => v.Length > 0))));
                    break;
                case "PRIORITY" or "SEQUENCE":
                    if (int.TryParse(value, out _)) comp.Properties.Add(new ContentLine(p.Name, value));
                    break;
                case var n when CalTextProps.Contains(n):
                    comp.Properties.Add(new ContentLine(n, n == "URL" ? value : EscapeText(value)));
                    break;
                case var n when n.StartsWith("X-", StringComparison.Ordinal):
                    comp.Properties.Add(new ContentLine(n, EscapeText(value)));
                    break;
            }
        }

        if (item.Value("RRULE") is { } rr && Vcal10RuleTo20(rr.Trim()) is { } converted20)
            comp.Properties.Add(new ContentLine("RRULE", converted20));

        // Alarms: DALARM (display) preferred over AALARM (audio).
        var alarm = item.Property("DALARM") ?? item.Property("AALARM");
        if (alarm != null)
        {
            var at = DecodedValue(alarm).Split(';')[0].Trim();
            if (at.Length > 0)
            {
                var valarm = new Component("VALARM");
                valarm.Properties.Add(new ContentLine("ACTION", alarm.Name == "AALARM" ? "AUDIO" : "DISPLAY"));
                var atUtc = ConvertTime(at, tzOffset);
                if (dtstartUtc != null && ICalTime.TryParse(atUtc, null, out var a) && a.IsUtc)
                    valarm.Properties.Add(new ContentLine("TRIGGER", FormatDuration(a.Value - dtstartUtc.Value)));
                else
                {
                    var trig = new ContentLine("TRIGGER", atUtc);
                    trig.Parameters.Add(new ContentParameter("VALUE", ["DATE-TIME"]));
                    valarm.Properties.Add(trig);
                }
                if (alarm.Name == "DALARM") valarm.Properties.Add(new ContentLine("DESCRIPTION", comp.Value("SUMMARY") ?? "Reminder"));
                comp.Children.Add(valarm);
            }
        }

        // Preserve what vCalendar 1.0 cannot carry.
        if (old != null)
        {
            foreach (var p in old.Properties.Where(p => p.Name is "ATTENDEE" or "ORGANIZER"))
                comp.Properties.Add(p);
            var oldRule = old.Value("RRULE");
            if (comp.Property("RRULE") == null && oldRule != null && Vcal20RuleTo10(oldRule, null) == null)
                comp.Properties.AddRange(old.Properties.Where(p => p.Name is "RRULE" or "EXDATE" or "RDATE"));
        }

        var cal = new Component("VCALENDAR");
        cal.Properties.Add(new ContentLine("VERSION", "2.0"));
        cal.Properties.Add(new ContentLine("PRODID", "-//Nullify//Nullify.CalDav SyncML//EN"));
        cal.Children.Add(comp);
        return ContentWriter.Write(cal);
    }

    private static void EnsureUid(Component vcalendar)
    {
        var comps = vcalendar.Children.Where(c => CalendarObjectInfo.ObjectComponents.Contains(c.Name)).ToList();
        if (comps.Count == 0 || comps.All(c => !string.IsNullOrWhiteSpace(c.Value("UID")))) return;
        var uid = comps.Select(c => c.Value("UID")).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? Guid.NewGuid().ToString();
        foreach (var c in comps.Where(c => string.IsNullOrWhiteSpace(c.Value("UID"))))
            c.Properties.Insert(0, new ContentLine("UID", uid));
    }

    private static ContentLine DateLine(string name, DateTime date)
    {
        var l = new ContentLine(name, date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        l.Parameters.Add(new ContentParameter("VALUE", ["DATE"]));
        return l;
    }

    private static string? NormalizeOffset(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return null;
        tz = tz.Trim().Replace(":", "");
        if (tz.Length == 3) tz += "00"; // "+01" → "+0100"
        return tz;
    }

    private static bool TryLocal(ContentLine? p, out DateTime value, out bool isUtc)
    {
        value = default;
        isUtc = false;
        if (p == null) return false;
        if (!ICalTime.TryParse(DecodedValue(p).Trim(), null, out var v)) return false;
        value = v.Value;
        isUtc = v.IsUtc;
        return true;
    }

    private static string ConvertTime(string value, TimeSpan? tzOffset)
    {
        if (!ICalTime.TryParse(value, null, out var v)) return value;
        if (v.IsDate || v.IsUtc || tzOffset == null) return value;
        return ICalTime.FormatUtc(DateTime.SpecifyKind(v.Value - tzOffset.Value, DateTimeKind.Utc));
    }

    private static string FormatDuration(TimeSpan d)
    {
        var sb = new StringBuilder();
        if (d < TimeSpan.Zero) { sb.Append('-'); d = d.Negate(); }
        sb.Append('P');
        if (d.Days > 0) sb.Append(d.Days).Append('D');
        if (d.Hours > 0 || d.Minutes > 0 || d.Seconds > 0 || d.Days == 0)
        {
            sb.Append('T');
            if (d.Hours > 0) sb.Append(d.Hours).Append('H');
            if (d.Minutes > 0) sb.Append(d.Minutes).Append('M');
            if (d.Seconds > 0 || (d.Hours == 0 && d.Minutes == 0)) sb.Append(d.Seconds).Append('S');
        }
        return sb.ToString();
    }

    private static string? MapStatusTo20(string comp, string status)
    {
        status = status.Trim().ToUpperInvariant().Replace(' ', '-');
        return comp == "VTODO"
            ? status switch
            {
                "COMPLETED" => "COMPLETED",
                "NEEDS-ACTION" or "SENT" or "DELEGATED" => "NEEDS-ACTION",
                "ACCEPTED" or "IN-PROCESS" => "IN-PROCESS",
                "DECLINED" or "CANCELLED" => "CANCELLED",
                _ => null,
            }
            : status switch
            {
                "CONFIRMED" or "ACCEPTED" => "CONFIRMED",
                "TENTATIVE" or "NEEDS-ACTION" or "SENT" => "TENTATIVE",
                "DECLINED" or "CANCELLED" => "CANCELLED",
                _ => null,
            };
    }

    /// <summary>Converts a stored iCalendar 2.0 object (VEVENT/VTODO) to vCalendar 1.0.</summary>
    public static string VCalendarTo10(string stored)
    {
        var root = CalendarObjectInfo.ParseCalendar(stored);
        var analyzer = new CalendarAnalyzer(root);
        var master = root.Children.FirstOrDefault(c => c.Name is "VEVENT" or "VTODO" && c.Property("RECURRENCE-ID") == null)
                     ?? root.Children.FirstOrDefault(c => c.Name is "VEVENT" or "VTODO")
                     ?? throw new InvalidObjectException("No VEVENT or VTODO");

        var comp = new Component(master.Name);
        ICalDateTime? start = null;
        if (ICalTime.TryParse(master.Property("DTSTART"), out var s0)) start = s0;

        foreach (var p in master.Properties)
        {
            switch (p.Name)
            {
                case "DTSTART" or "DTEND" or "DUE" or "COMPLETED" or "CREATED" or "LAST-MODIFIED":
                {
                    if (!ICalTime.TryParse(p, out var v)) break;
                    var name = p.Name == "CREATED" ? "DCREATED" : p.Name;
                    string text;
                    if (v.IsDate)
                    {
                        var d = v.Value;
                        text = p.Name == "DTEND" ? d.AddDays(-1).ToString("yyyyMMdd'T'235900") : d.ToString("yyyyMMdd'T'000000");
                    }
                    else text = v.IsFloating ? v.Value.ToString("yyyyMMdd'T'HHmmss") : ICalTime.FormatUtc(analyzer.ToUtc(v));
                    comp.Properties.Add(new ContentLine(name, text));
                    break;
                }
                case "EXDATE":
                {
                    var values = ICalTime.ParseList(p).Select(v => v.IsDate ? v.Value.ToString("yyyyMMdd'T'000000")
                        : v.IsFloating ? v.Value.ToString("yyyyMMdd'T'HHmmss") : ICalTime.FormatUtc(analyzer.ToUtc(v)));
                    comp.Properties.Add(new ContentLine("EXDATE", string.Join(';', values)));
                    break;
                }
                case "RRULE":
                    if (Vcal20RuleTo10(p.Value, start?.Value) is { } r10) comp.Properties.Add(new ContentLine("RRULE", r10));
                    break;
                case "STATUS":
                    comp.Properties.Add(new ContentLine("STATUS", p.Value.Trim().ToUpperInvariant() switch
                    {
                        "NEEDS-ACTION" => "NEEDS ACTION",
                        "IN-PROCESS" => "ACCEPTED",
                        "CANCELLED" => "DECLINED",
                        var x => x,
                    }));
                    break;
                case "TRANSP":
                    comp.Properties.Add(new ContentLine("TRANSP", p.Value.Trim().Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? "1" : "0"));
                    break;
                case "CATEGORIES":
                    comp.Properties.Add(LegacyText("CATEGORIES", string.Join(';', SplitUnescaped(p.Value, ',').Select(ContentLine.UnescapeText))));
                    break;
                case "PRIORITY" or "SEQUENCE" or "UID":
                    comp.Properties.Add(new ContentLine(p.Name, p.Value.Trim()));
                    break;
                case var n when CalTextProps.Contains(n):
                    comp.Properties.Add(LegacyText(n, p.TextValue));
                    break;
            }
        }

        if (start is { IsDate: true }) comp.Properties.Add(new ContentLine("X-FUNAMBOL-ALLDAY", "1"));

        var alarm = master.ChildrenNamed("VALARM").FirstOrDefault();
        if (alarm?.Property("TRIGGER") is { } trigger)
        {
            DateTime? at = null;
            if (string.Equals(trigger.GetParameter("VALUE"), "DATE-TIME", StringComparison.OrdinalIgnoreCase) &&
                ICalTime.TryParse(trigger.Value, null, out var abs))
                at = analyzer.ToUtc(abs);
            else if (ICalTime.TryParseDuration(trigger.Value, out var offset) && start != null)
                at = analyzer.ToUtc(start.Value) + offset;
            if (at != null)
            {
                var name = string.Equals(alarm.Value("ACTION"), "AUDIO", StringComparison.OrdinalIgnoreCase) ? "AALARM" : "DALARM";
                comp.Properties.Add(new ContentLine(name, ICalTime.FormatUtc(at.Value) + ";;;"));
                if (name == "DALARM") comp.Properties.Add(new ContentLine("AALARM", ICalTime.FormatUtc(at.Value) + ";;;"));
            }
        }

        var cal = new Component("VCALENDAR");
        cal.Properties.Add(new ContentLine("VERSION", "1.0"));
        cal.Properties.Add(new ContentLine("PRODID", "-//Nullify//Nullify.CalDav//EN"));
        cal.Children.Add(comp);
        return WriteLegacy(cal);
    }

    // ------------------------------------------------------------------ Recurrence rule grammar (vCalendar 1.0 §3.9.6)

    private static readonly string[] Days = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static string? Vcal10RuleTo20(string rule)
    {
        var tokens = rule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var head = tokens[0].ToUpperInvariant();
        var freqCode = new string(head.TakeWhile(char.IsLetter).ToArray());
        if (!int.TryParse(head[freqCode.Length..], out var interval) || interval < 1) interval = 1;

        string? end = "COUNT=2"; // the vCalendar default duration is two occurrences
        var modifiers = new List<string>();
        foreach (var t in tokens.Skip(1))
        {
            if (t.StartsWith('#'))
            {
                end = int.TryParse(t[1..], out var n) && n > 0 ? $"COUNT={n}" : null;
            }
            else if (t.Length >= 8 && char.IsDigit(t[0]) && t.Contains('T') || t.Length == 8 && t.All(char.IsDigit))
            {
                end = "UNTIL=" + t;
            }
            else modifiers.Add(t.ToUpperInvariant());
        }

        var sb = new StringBuilder();
        switch (freqCode)
        {
            case "D":
                sb.Append("FREQ=DAILY");
                break;
            case "W":
                sb.Append("FREQ=WEEKLY");
                if (modifiers.Count > 0) sb.Append(";BYDAY=").Append(string.Join(',', modifiers.Where(Days.Contains)));
                break;
            case "MP":
            {
                sb.Append("FREQ=MONTHLY");
                var byDay = new List<string>();
                var ordinals = new List<int>();
                foreach (var m in modifiers)
                {
                    if (Days.Contains(m))
                    {
                        foreach (var o in ordinals.DefaultIfEmpty(0)) byDay.Add((o == 0 ? "" : o.ToString(CultureInfo.InvariantCulture)) + m);
                        ordinals.Clear();
                    }
                    else if (m.Length >= 2 && int.TryParse(m[..^1], out var o))
                        ordinals.Add(m.EndsWith('-') ? -o : o);
                }
                if (byDay.Count > 0) sb.Append(";BYDAY=").Append(string.Join(',', byDay));
                break;
            }
            case "MD":
            {
                sb.Append("FREQ=MONTHLY");
                var days = modifiers.Select(m => m == "LD" ? "-1" : m.EndsWith('-') ? "-" + m[..^1] : m.TrimEnd('+'))
                    .Where(m => int.TryParse(m, out _)).ToList();
                if (days.Count > 0) sb.Append(";BYMONTHDAY=").Append(string.Join(',', days));
                break;
            }
            case "YM":
                sb.Append("FREQ=YEARLY");
                if (modifiers.Count > 0) sb.Append(";BYMONTH=").Append(string.Join(',', modifiers.Where(m => int.TryParse(m, out _))));
                break;
            case "YD":
                sb.Append("FREQ=YEARLY");
                if (modifiers.Count > 0) sb.Append(";BYYEARDAY=").Append(string.Join(',', modifiers.Where(m => int.TryParse(m, out _))));
                break;
            default:
                return null;
        }
        if (interval > 1) sb.Append(";INTERVAL=").Append(interval);
        if (end != null) sb.Append(';').Append(end);
        return sb.ToString().Replace(";BYDAY=;", ";").Replace(";BYMONTH=;", ";");
    }

    /// <summary>Converts an RRULE to vCalendar 1.0 syntax, or null when it cannot be represented.</summary>
    public static string? Vcal20RuleTo10(string rrule, DateTime? start)
    {
        if (!RecurrenceRule.TryParse(rrule, out var r)) return null;
        if (r.BySetPos.Count > 0 || r.ByHour.Count > 0 || r.ByMinute.Count > 0 || r.BySecond.Count > 0 || r.ByWeekNo.Count > 0)
            return null;
        string body;
        switch (r.Freq)
        {
            case Frequency.Daily when r.ByDay.Count == 0 && r.ByMonth.Count == 0 && r.ByMonthDay.Count == 0:
                body = $"D{r.Interval}";
                break;
            case Frequency.Weekly when r.ByDay.All(d => d.Ordinal == 0) && r.ByMonth.Count == 0 && r.ByMonthDay.Count == 0:
                body = $"W{r.Interval} " + string.Join(' ', (r.ByDay.Count > 0 ? r.ByDay.Select(d => d.Day) : start != null ? [start.Value.DayOfWeek] : [])
                    .Select(d => Days[(int)d]));
                break;
            case Frequency.Monthly when r.ByDay.Count > 0 && r.ByMonthDay.Count == 0 && r.ByMonth.Count == 0:
                if (r.ByDay.Any(d => d.Ordinal == 0)) return null;
                body = $"MP{r.Interval} " + string.Join(' ', r.ByDay.Select(d => $"{Math.Abs(d.Ordinal)}{(d.Ordinal < 0 ? "-" : "+")} {Days[(int)d.Day]}"));
                break;
            case Frequency.Monthly when r.ByDay.Count == 0 && r.ByMonth.Count == 0:
                var mdays = r.ByMonthDay.Count > 0 ? r.ByMonthDay : start != null ? [start.Value.Day] : [];
                body = $"MD{r.Interval} " + string.Join(' ', mdays.Select(d => d < 0 ? $"{-d}-" : d.ToString(CultureInfo.InvariantCulture)));
                break;
            case Frequency.Yearly when r.ByDay.Count == 0 && r.ByMonthDay.Count == 0 && r.ByYearDay.Count == 0:
                var months = r.ByMonth.Count > 0 ? r.ByMonth : start != null ? [start.Value.Month] : [];
                body = $"YM{r.Interval} " + string.Join(' ', months);
                break;
            case Frequency.Yearly when r.ByYearDay.Count > 0 && r.ByDay.Count == 0 && r.ByMonth.Count == 0:
                body = $"YD{r.Interval} " + string.Join(' ', r.ByYearDay);
                break;
            default:
                return null;
        }
        var endPart = r.Count is { } c ? $"#{c}" : r.Until != null ? NormalizeUntil(r.Until) : "#0";
        return (body.TrimEnd() + " " + endPart).Trim();
    }

    private static string NormalizeUntil(string until) =>
        until.Length == 8 ? until + "T235959" : until;

    // ------------------------------------------------------------------ Notes (vNote 1.1 / plain text ↔ VJOURNAL)

    public static string NoteToJournal(string text, string contentType, string? existing)
    {
        string body;
        string? summary = null;
        if (contentType.Contains("vnote", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("BEGIN:VNOTE", StringComparison.OrdinalIgnoreCase))
        {
            var note = ParseLegacy(text).FirstOrDefault(c => c.Name == "VNOTE") ?? throw new InvalidObjectException("Expected a VNOTE");
            body = note.Property("BODY") is { } b ? ContentLine.UnescapeText(DecodedValue(b)) : "";
            summary = note.Property("SUMMARY") is { } s ? ContentLine.UnescapeText(DecodedValue(s)) : null;
        }
        else body = text;

        body = body.Replace("\r\n", "\n").TrimEnd('\0');
        summary ??= body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Note";
        if (summary.Length > 120) summary = summary[..120];

        string? uid = null;
        if (existing != null)
        {
            try { uid = CalendarObjectInfo.ParseCalendar(existing).Children.FirstOrDefault(c => c.Name == "VJOURNAL")?.Value("UID"); }
            catch (InvalidObjectException) { }
        }

        var journal = new Component("VJOURNAL");
        journal.Properties.Add(new ContentLine("UID", uid ?? Guid.NewGuid().ToString()));
        journal.Properties.Add(new ContentLine("DTSTAMP", ICalTime.FormatUtc(DateTime.UtcNow)));
        journal.Properties.Add(new ContentLine("SUMMARY", EscapeText(summary)));
        journal.Properties.Add(new ContentLine("DESCRIPTION", EscapeText(body)));
        var cal = new Component("VCALENDAR");
        cal.Properties.Add(new ContentLine("VERSION", "2.0"));
        cal.Properties.Add(new ContentLine("PRODID", "-//Nullify//Nullify.CalDav SyncML//EN"));
        cal.Children.Add(journal);
        return ContentWriter.Write(cal);
    }

    public static string JournalToText(string stored)
    {
        var j = CalendarObjectInfo.ParseCalendar(stored).Children.First(c => c.Name == "VJOURNAL");
        var description = j.Property("DESCRIPTION")?.TextValue;
        var summary = j.Property("SUMMARY")?.TextValue;
        if (string.IsNullOrEmpty(description)) return summary ?? "";
        if (string.IsNullOrEmpty(summary) || description.StartsWith(summary, StringComparison.Ordinal)) return description;
        return summary + "\n" + description;
    }

    public static string JournalToVNote(string stored)
    {
        var j = CalendarObjectInfo.ParseCalendar(stored).Children.First(c => c.Name == "VJOURNAL");
        var note = new Component("VNOTE");
        note.Properties.Add(new ContentLine("VERSION", "1.1"));
        note.Properties.Add(LegacyText("BODY", JournalToText(stored)));
        if (j.Property("SUMMARY")?.TextValue is { Length: > 0 } s) note.Properties.Add(LegacyText("SUMMARY", s));
        if (j.Property("CATEGORIES") is { } cats) note.Properties.Add(LegacyText("CATEGORIES", cats.TextValue));
        return WriteLegacy(note);
    }
}
