// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

namespace Nullify.CalDav.Formats;

public sealed class InvalidObjectException(string message) : Exception(message);

/// <summary>Validated, indexed view of a calendar object resource.</summary>
public sealed class CalendarObjectInfo
{
    public static readonly string[] ObjectComponents = ["VEVENT", "VTODO", "VJOURNAL", "VFREEBUSY"];

    public required Component Root { get; init; }
    public required string ComponentType { get; init; }
    public required string Uid { get; init; }
    public long? FirstOccurrence { get; init; }
    public long? LastOccurrence { get; init; }

    public static Component ParseCalendar(string data)
    {
        List<Component> roots;
        try { roots = ContentReader.Parse(data); }
        catch (ContentParseException ex) { throw new InvalidObjectException(ex.Message); }
        if (roots.Count != 1 || roots[0].Name != "VCALENDAR")
            throw new InvalidObjectException("Expected exactly one VCALENDAR component");
        return roots[0];
    }

    /// <summary>Validates a calendar object resource per RFC 4791 4.1.</summary>
    public static CalendarObjectInfo Analyze(string data)
    {
        var root = ParseCalendar(data);
        var comps = root.Children.Where(c => ObjectComponents.Contains(c.Name)).ToList();
        if (comps.Count == 0)
            throw new InvalidObjectException("Calendar object contains no VEVENT, VTODO, VJOURNAL or VFREEBUSY");
        var type = comps[0].Name;
        if (comps.Any(c => c.Name != type))
            throw new InvalidObjectException("Calendar object mixes component types");

        var uids = comps.Select(c => c.Value("UID")?.Trim()).Distinct().ToList();
        if (uids.Count != 1 || string.IsNullOrEmpty(uids[0]))
        {
            if (uids.Count > 1) throw new InvalidObjectException("All components must share the same UID");
            if (type != "VFREEBUSY") throw new InvalidObjectException("Missing UID");
        }

        var analyzer = new CalendarAnalyzer(root);
        var (first, last) = analyzer.GetBounds();
        return new CalendarObjectInfo
        {
            Root = root,
            ComponentType = type,
            Uid = uids[0] ?? "",
            FirstOccurrence = first is { } f && f != DateTime.MinValue ? ToUnix(f) : null,
            LastOccurrence = last is { } l && l != DateTime.MaxValue ? ToUnix(l) : null,
        };
    }

    public static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

public sealed class CardInfo
{
    public required Component Root { get; init; }
    public required string Uid { get; init; }
    public string? FormattedName { get; init; }

    public static CardInfo Analyze(string data, string fallbackUid)
    {
        List<Component> roots;
        try { roots = ContentReader.Parse(data); }
        catch (ContentParseException ex) { throw new InvalidObjectException(ex.Message); }
        if (roots.Count != 1 || roots[0].Name != "VCARD")
            throw new InvalidObjectException("Expected exactly one VCARD");
        var card = roots[0];
        var version = card.Value("VERSION")?.Trim();
        if (version is not ("3.0" or "4.0" or "2.1" or null))
            throw new InvalidObjectException("Unsupported vCard version");
        var uid = card.Value("UID")?.Trim();
        return new CardInfo
        {
            Root = card,
            Uid = string.IsNullOrEmpty(uid) ? fallbackUid : uid,
            FormattedName = card.Property("FN")?.TextValue,
        };
    }
}
