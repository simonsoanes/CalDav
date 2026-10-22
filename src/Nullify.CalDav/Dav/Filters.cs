using System.Xml.Linq;
using Nullify.CalDav.Formats;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

public sealed class TextMatch
{
    public string Text { get; init; } = "";
    public string Collation { get; init; } = "i;ascii-casemap";
    public bool Negate { get; init; }
    public string MatchType { get; init; } = "contains";

    public static TextMatch Parse(XElement e, string defaultCollation)
    {
        var collation = (string?)e.Attribute("collation") ?? defaultCollation;
        if (collation is not ("i;ascii-casemap" or "i;octet" or "i;unicode-casemap"))
        {
            var ns = e.Name.Namespace;
            throw new DavException(403, ns + "supported-collation", "Unsupported collation " + collation);
        }
        return new TextMatch
        {
            Text = e.Value,
            Collation = collation,
            Negate = string.Equals((string?)e.Attribute("negate-condition"), "yes", StringComparison.OrdinalIgnoreCase),
            MatchType = (string?)e.Attribute("match-type") ?? "contains",
        };
    }

    public bool Matches(string value)
    {
        var cmp = Collation == "i;octet" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = MatchType switch
        {
            "equals" => string.Equals(value, Text, cmp),
            "starts-with" => value.StartsWith(Text, cmp),
            "ends-with" => value.EndsWith(Text, cmp),
            _ => value.Contains(Text, cmp),
        };
        return result != Negate;
    }
}

public readonly record struct TimeRange(DateTime Start, DateTime End)
{
    public static TimeRange? Parse(XElement? e)
    {
        if (e == null) return null;
        var start = DateTime.MinValue;
        var end = DateTime.MaxValue;
        var s = (string?)e.Attribute("start");
        var en = (string?)e.Attribute("end");
        if (s == null && en == null) throw new DavException(400, message: "time-range requires start or end");
        if (s != null)
        {
            if (!ICalTime.TryParse(s, null, out var v) || !v.IsUtc) throw new DavException(400, message: "Invalid time-range start");
            start = v.Value;
        }
        if (en != null)
        {
            if (!ICalTime.TryParse(en, null, out var v) || !v.IsUtc) throw new DavException(400, message: "Invalid time-range end");
            end = v.Value;
        }
        return new TimeRange(start, end);
    }
}

/// <summary>CALDAV:filter (RFC 4791 9.7).</summary>
public sealed class CompFilter
{
    public string Name { get; init; } = "";
    public bool IsNotDefined { get; init; }
    public TimeRange? TimeRange { get; init; }
    public List<CompFilter> Comps { get; } = [];
    public List<PropFilter> Props { get; } = [];

    public static CompFilter ParseRoot(XElement filter)
    {
        var root = filter.Element(C + "comp-filter") ?? throw new DavException(403, C + "valid-filter", "Missing comp-filter");
        var f = Parse(root);
        if (f.Name != "VCALENDAR") throw new DavException(403, C + "valid-filter", "Top-level comp-filter must be VCALENDAR");
        return f;
    }

    private static CompFilter Parse(XElement e)
    {
        var f = new CompFilter
        {
            Name = ((string?)e.Attribute("name") ?? "").ToUpperInvariant(),
            IsNotDefined = e.Element(C + "is-not-defined") != null,
            TimeRange = Dav.TimeRange.Parse(e.Element(C + "time-range")),
        };
        f.Comps.AddRange(e.Elements(C + "comp-filter").Select(Parse));
        f.Props.AddRange(e.Elements(C + "prop-filter").Select(PropFilter.Parse));
        return f;
    }

    /// <summary>Returns the component type and time range used to pre-filter in the database, if any.</summary>
    public (string? Component, TimeRange? Range) Prefilter()
    {
        if (Comps.Count != 1 || Comps[0].IsNotDefined) return (null, null);
        var inner = Comps[0];
        return (inner.Name, inner.TimeRange);
    }

    public bool Matches(Component vcalendar, CalendarAnalyzer analyzer) =>
        vcalendar.Name == Name && MatchesContent(vcalendar, analyzer, null);

    private bool MatchesContent(Component comp, CalendarAnalyzer analyzer, Component? parent)
    {
        foreach (var p in Props)
            if (!p.Matches(comp))
                return false;
        foreach (var cf in Comps)
            if (!cf.MatchesIn(comp, analyzer))
                return false;
        return true;
    }

    private bool MatchesIn(Component parent, CalendarAnalyzer analyzer)
    {
        var candidates = parent.ChildrenNamed(Name).ToList();
        if (IsNotDefined) return candidates.Count == 0;
        if (candidates.Count == 0) return false;

        HashSet<Component>? inRange = null;
        if (TimeRange is { } tr)
        {
            if (Name == "VALARM")
                inRange = candidates.Where(a => AlarmInRange(a, parent, analyzer, tr)).ToHashSet();
            else if (parent.Name == "VCALENDAR")
                inRange = analyzer.GetInstances(Name, tr.Start, tr.End).Select(i => i.Source).ToHashSet();
            else
                inRange = candidates.ToHashSet(); // time-range on other nested components is not evaluated
        }

        return candidates.Any(c => (inRange == null || inRange.Contains(c)) && MatchesContent(c, analyzer, parent));
    }

    private static bool AlarmInRange(Component alarm, Component parent, CalendarAnalyzer analyzer, TimeRange tr)
    {
        var trigger = alarm.Property("TRIGGER");
        if (trigger == null) return false;
        if (string.Equals(trigger.GetParameter("VALUE"), "DATE-TIME", StringComparison.OrdinalIgnoreCase))
        {
            if (!ICalTime.TryParse(trigger.Value, null, out var abs)) return false;
            var t = analyzer.ToUtc(abs);
            return tr.Start <= t && t < tr.End;
        }
        if (!ICalTime.TryParseDuration(trigger.Value, out var offset)) return false;
        var related = trigger.GetParameter("RELATED");
        var fromEnd = string.Equals(related, "END", StringComparison.OrdinalIgnoreCase);

        // Expand the parent over the window in which an instance could trigger inside the range.
        var pad = offset.Duration() + TimeSpan.FromDays(366);
        var ws = tr.Start == DateTime.MinValue ? DateTime.MinValue : tr.Start - pad;
        var we = tr.End == DateTime.MaxValue ? DateTime.MaxValue : tr.End + pad;
        foreach (var inst in analyzer.GetInstances(parent.Name, ws, we).Where(i => i.Source == parent))
        {
            var basis = fromEnd ? inst.EndUtc : inst.StartUtc;
            if (basis == DateTime.MinValue || basis == DateTime.MaxValue) continue;
            var t = basis + offset;
            if (tr.Start <= t && t < tr.End) return true;
        }
        return false;
    }
}

public sealed class PropFilter
{
    public string Name { get; init; } = "";
    public bool IsNotDefined { get; init; }
    public TimeRange? TimeRange { get; init; }
    public TextMatch? TextMatch { get; init; }
    public List<ParamFilter> Params { get; } = [];

    public static PropFilter Parse(XElement e)
    {
        var f = new PropFilter
        {
            Name = ((string?)e.Attribute("name") ?? "").ToUpperInvariant(),
            IsNotDefined = e.Element(C + "is-not-defined") != null,
            TimeRange = Dav.TimeRange.Parse(e.Element(C + "time-range")),
            TextMatch = e.Element(C + "text-match") is { } tm ? Dav.TextMatch.Parse(tm, "i;ascii-casemap") : null,
        };
        f.Params.AddRange(e.Elements(C + "param-filter").Select(ParamFilter.Parse));
        return f;
    }

    public bool Matches(Component comp)
    {
        var props = comp.PropertiesNamed(Name).ToList();
        if (IsNotDefined) return props.Count == 0;
        return props.Any(p =>
        {
            if (TextMatch != null && !TextMatch.Matches(p.TextValue)) return false;
            if (TimeRange is { } tr)
            {
                if (!ICalTime.TryParse(p, out var v)) return false;
                var t = new TimeZoneResolver(null).ToUtc(v);
                if (!(tr.Start <= t && t < tr.End)) return false;
            }
            return Params.All(pf => pf.Matches(p));
        });
    }
}

public sealed class ParamFilter
{
    public string Name { get; init; } = "";
    public bool IsNotDefined { get; init; }
    public TextMatch? TextMatch { get; init; }

    public static ParamFilter Parse(XElement e)
    {
        var ns = e.Name.Namespace;
        return new ParamFilter
        {
            Name = ((string?)e.Attribute("name") ?? "").ToUpperInvariant(),
            IsNotDefined = e.Element(ns + "is-not-defined") != null,
            TextMatch = e.Element(ns + "text-match") is { } tm
                ? Dav.TextMatch.Parse(tm, ns == CR ? "i;unicode-casemap" : "i;ascii-casemap")
                : null,
        };
    }

    public bool Matches(ContentLine line)
    {
        var values = line.Parameters.Where(p => p.Name == Name).SelectMany(p => p.Values).ToList();
        var exists = line.Parameters.Any(p => p.Name == Name);
        if (IsNotDefined) return !exists;
        if (!exists) return false;
        return TextMatch == null || values.Any(TextMatch.Matches);
    }
}

/// <summary>CARDDAV:filter (RFC 6352 10.5).</summary>
public sealed class CardFilter
{
    public bool AllOf { get; init; }
    public List<CardPropFilter> Props { get; } = [];

    public static CardFilter Parse(XElement? filter)
    {
        var f = new CardFilter { AllOf = string.Equals((string?)filter?.Attribute("test"), "allof", StringComparison.OrdinalIgnoreCase) };
        if (filter != null) f.Props.AddRange(filter.Elements(CR + "prop-filter").Select(CardPropFilter.Parse));
        return f;
    }

    public bool Matches(Component vcard)
    {
        if (Props.Count == 0) return true;
        return AllOf ? Props.All(p => p.Matches(vcard)) : Props.Any(p => p.Matches(vcard));
    }
}

public sealed class CardPropFilter
{
    public string Name { get; init; } = "";
    public bool AllOf { get; init; }
    public bool IsNotDefined { get; init; }
    public List<TextMatch> TextMatches { get; } = [];
    public List<ParamFilter> Params { get; } = [];

    public static CardPropFilter Parse(XElement e)
    {
        var f = new CardPropFilter
        {
            Name = ((string?)e.Attribute("name") ?? "").ToUpperInvariant(),
            AllOf = string.Equals((string?)e.Attribute("test"), "allof", StringComparison.OrdinalIgnoreCase),
            IsNotDefined = e.Element(CR + "is-not-defined") != null,
        };
        f.TextMatches.AddRange(e.Elements(CR + "text-match").Select(t => TextMatch.Parse(t, "i;unicode-casemap")));
        f.Params.AddRange(e.Elements(CR + "param-filter").Select(ParamFilter.Parse));
        return f;
    }

    public bool Matches(Component vcard)
    {
        var props = vcard.PropertiesNamed(Name).ToList();
        if (IsNotDefined) return props.Count == 0;
        if (props.Count == 0) return false;
        if (TextMatches.Count == 0 && Params.Count == 0) return true;

        return props.Any(p =>
        {
            var tests = TextMatches.Select(t => t.Matches(p.TextValue)).Concat(Params.Select(pf => pf.Matches(p)));
            return AllOf ? tests.All(x => x) : tests.Any(x => x);
        });
    }
}
