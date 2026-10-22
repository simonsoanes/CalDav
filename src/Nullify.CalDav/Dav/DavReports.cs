// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

public sealed partial class DavHandler
{
    private async Task ReportAsync()
    {
        var doc = await DavXml.ReadBodyAsync(Request, Ct);
        var root = doc?.Root ?? throw new DavException(400, message: "REPORT requires a body");
        var node = await RequireNodeAsync();

        if (root.Name == C + "calendar-multiget") await CalendarMultiGetAsync(node, root);
        else if (root.Name == C + "calendar-query") await CalendarQueryAsync(node, root);
        else if (root.Name == C + "free-busy-query") await FreeBusyQueryAsync(node, root);
        else if (root.Name == CR + "addressbook-multiget") await AddressBookMultiGetAsync(node, root);
        else if (root.Name == CR + "addressbook-query") await AddressBookQueryAsync(node, root);
        else if (root.Name == D + "sync-collection") await SyncCollectionAsync(node, root);
        else if (root.Name == D + "principal-property-search") await PrincipalPropertySearchAsync(root);
        else if (root.Name == D + "principal-search-property-set") await PrincipalSearchPropertySetAsync();
        else if (root.Name == D + "principal-match") await PrincipalMatchAsync(root);
        else if (root.Name == D + "expand-property") await ExpandPropertyAsync(node, root);
        else throw new DavException(403, D + "supported-report", "Unsupported report " + root.Name);
    }

    private List<XElement> RequestedProps(XElement root) =>
        root.Element(D + "prop")?.Elements().ToList() ?? [new XElement(D + "getetag")];

    private async Task AddResponseAsync(MultiStatus ms, DavNode node, List<XElement> props)
    {
        var values = await _props.GetManyAsync(node, props, Ct);
        ms.AddPropStat(node.Href, Filter(values));
    }

    // ---------------------------------------------------------------- CalDAV

    private static CalendarNode CalendarOf(DavNode node) => node switch
    {
        CalendarNode c => c,
        CalendarObjectNode o => o.Calendar,
        _ => throw new DavException(403, D + "supported-report", "Report must target a calendar collection"),
    };

    private async Task CalendarMultiGetAsync(DavNode node, XElement root)
    {
        var cal = CalendarOf(node);
        var props = RequestedProps(root);
        var hrefs = root.Elements(D + "href").Select(h => h.Value.Trim()).Where(h => h.Length > 0).ToList();

        // Batch-load everything that lives in the target calendar.
        var local = new Dictionary<string, string>();
        foreach (var href in hrefs)
        {
            var segs = Paths.ParseHref(href);
            if (segs is { Length: 4 } && segs[0] == "calendars" &&
                string.Equals(segs[1], cal.HomeUser.Username, StringComparison.OrdinalIgnoreCase) &&
                Paths.Calendar(cal.HomeUser.Username, segs[2]) == cal.Href)
                local[href] = segs[3];
        }
        var uris = local.Values.Distinct().ToList();
        var objects = await _db.CalendarObjects.AsNoTracking()
            .Where(o => o.CalendarId == cal.Calendar.Id && uris.Contains(o.Uri)).ToDictionaryAsync(o => o.Uri, Ct);

        var ms = new MultiStatus();
        foreach (var href in hrefs)
        {
            DavNode? target = null;
            if (local.TryGetValue(href, out var uri))
            {
                if (objects.TryGetValue(uri, out var obj)) target = _resolver.ObjectNode(cal, obj);
            }
            else
            {
                target = await ResolveHrefAsync(href);
                if (target is not CalendarObjectNode) target = null;
            }

            if (target == null) ms.AddStatus(href, 404);
            else await AddResponseAsync(ms, target, props);
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private async Task<List<CalendarObject>> QueryCalendarObjectsAsync(Calendar cal, string? component, TimeRange? range)
    {
        var q = _db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == cal.Id);
        if (component != null) q = q.Where(o => o.ComponentType == component);
        if (range is { } r)
        {
            if (r.End != DateTime.MaxValue)
            {
                var end = CalendarObjectInfo.ToUnix(r.End);
                q = q.Where(o => o.FirstOccurrence == null || o.FirstOccurrence < end);
            }
            if (r.Start != DateTime.MinValue)
            {
                var start = CalendarObjectInfo.ToUnix(r.Start);
                q = q.Where(o => o.LastOccurrence == null || o.LastOccurrence >= start);
            }
        }
        return await q.ToListAsync(Ct);
    }

    private async Task CalendarQueryAsync(DavNode node, XElement root)
    {
        var cal = CalendarOf(node);
        var props = RequestedProps(root);
        var filterEl = root.Element(C + "filter");
        var filter = filterEl == null ? null : CompFilter.ParseRoot(filterEl);
        var (component, range) = filter?.Prefilter() ?? (null, null);

        List<CalendarObject> candidates;
        if (node is CalendarObjectNode single)
        {
            await _resolver.GetDataAsync(single, Ct);
            candidates = [single.Object];
        }
        else candidates = await QueryCalendarObjectsAsync(cal.Calendar, component, range);

        var ms = new MultiStatus();
        foreach (var obj in candidates)
        {
            if (filter != null)
            {
                Component parsed;
                try { parsed = CalendarObjectInfo.ParseCalendar(obj.Data); }
                catch (InvalidObjectException) { continue; }
                if (!filter.Matches(parsed, new CalendarAnalyzer(parsed))) continue;
            }
            await AddResponseAsync(ms, _resolver.ObjectNode(cal, obj), props);
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private async Task FreeBusyQueryAsync(DavNode node, XElement root)
    {
        if (node is not CalendarNode cal) throw new DavException(403, D + "supported-report");
        var range = TimeRange.Parse(root.Element(C + "time-range")) ?? throw new DavException(400, message: "time-range required");
        if (range.Start == DateTime.MinValue || range.End == DateTime.MaxValue)
            throw new DavException(400, message: "time-range requires both start and end");

        var datas = (await QueryCalendarObjectsAsync(cal.Calendar, "VEVENT", range)).Select(o => o.Data);
        var ics = FreeBusy.Write(FreeBusy.Compute(datas, range.Start, range.End), range.Start, range.End);
        var bytes = Encoding.UTF8.GetBytes(ics);
        Response.StatusCode = 200;
        Response.ContentType = "text/calendar; charset=utf-8";
        Response.ContentLength = bytes.Length;
        await Response.Body.WriteAsync(bytes, Ct);
    }

    // ---------------------------------------------------------------- CardDAV

    private static AddressBookNode BookOf(DavNode node) => node switch
    {
        AddressBookNode b => b,
        CardNode c => c.Book,
        _ => throw new DavException(403, D + "supported-report", "Report must target an address book collection"),
    };

    private async Task AddressBookMultiGetAsync(DavNode node, XElement root)
    {
        var book = BookOf(node);
        var props = RequestedProps(root);
        var hrefs = root.Elements(D + "href").Select(h => h.Value.Trim()).Where(h => h.Length > 0).ToList();

        var local = new Dictionary<string, string>();
        foreach (var href in hrefs)
        {
            var segs = Paths.ParseHref(href);
            if (segs is { Length: 4 } && segs[0] == "addressbooks" &&
                string.Equals(segs[1], _user.Username, StringComparison.OrdinalIgnoreCase) &&
                Paths.AddressBook(_user.Username, segs[2]) == book.Href)
                local[href] = segs[3];
        }
        var uris = local.Values.Distinct().ToList();
        var cards = await _db.Cards.AsNoTracking()
            .Where(c => c.AddressBookId == book.Book.Id && uris.Contains(c.Uri)).ToDictionaryAsync(c => c.Uri, Ct);

        var ms = new MultiStatus();
        foreach (var href in hrefs)
        {
            DavNode? target = null;
            if (local.TryGetValue(href, out var uri))
            {
                if (cards.TryGetValue(uri, out var card)) target = _resolver.CardNode(book, card);
            }
            else
            {
                target = await ResolveHrefAsync(href);
                if (target is not CardNode) target = null;
            }
            if (target == null) ms.AddStatus(href, 404);
            else await AddResponseAsync(ms, target, props);
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private async Task AddressBookQueryAsync(DavNode node, XElement root)
    {
        var book = BookOf(node);
        var props = RequestedProps(root);
        var filter = CardFilter.Parse(root.Element(CR + "filter"));
        var limitText = root.Element(CR + "limit")?.Element(CR + "nresults")?.Value;
        var limit = int.TryParse(limitText, out var l) && l > 0 ? l : int.MaxValue;

        List<Card> candidates;
        if (node is CardNode single)
        {
            await _resolver.GetDataAsync(single, Ct);
            candidates = [single.Card];
        }
        else candidates = await _db.Cards.AsNoTracking().Where(c => c.AddressBookId == book.Book.Id).OrderBy(c => c.Id).ToListAsync(Ct);

        var ms = new MultiStatus();
        var truncated = false;
        foreach (var card in candidates)
        {
            if (filter.Props.Count > 0)
            {
                List<Component> parsed;
                try { parsed = ContentReader.Parse(card.Data); }
                catch (ContentParseException) { continue; }
                if (parsed.Count == 0 || !filter.Matches(parsed[0])) continue;
            }
            if (ms.Count >= limit)
            {
                truncated = true;
                break;
            }
            await AddResponseAsync(ms, _resolver.CardNode(book, card), props);
        }
        if (truncated) ms.AddStatus(book.Href, 507, DavXml.Error(D + "number-of-matches-within-limits"));
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    // ---------------------------------------------------------------- RFC 6578 sync-collection

    private async Task SyncCollectionAsync(DavNode node, XElement root)
    {
        var props = RequestedProps(root);
        var tokenText = root.Element(D + "sync-token")?.Value.Trim();
        var limitText = root.Element(D + "limit")?.Element(D + "nresults")?.Value;
        var limit = int.TryParse(limitText, out var l) && l > 0 ? l : int.MaxValue;

        long since = 0;
        if (!string.IsNullOrEmpty(tokenText) && !DavPropertyProvider.TryParseSyncToken(tokenText, out since))
            throw new DavException(403, D + "valid-sync-token", "Invalid sync token");

        var ms = new MultiStatus();
        switch (node)
        {
            case CalendarNode cal:
            {
                var current = cal.Calendar.SyncToken;
                if (since > current) throw new DavException(403, D + "valid-sync-token", "Sync token is from the future");
                long newToken = current;

                if (since == 0)
                {
                    var all = await _db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == cal.Calendar.Id).ToListAsync(Ct);
                    foreach (var o in all) await AddResponseAsync(ms, _resolver.ObjectNode(cal, o), props);
                }
                else
                {
                    var changes = await _db.CalendarChanges.AsNoTracking()
                        .Where(c => c.CalendarId == cal.Calendar.Id && c.SyncToken > since)
                        .OrderBy(c => c.SyncToken).Select(c => new { c.Uri, c.SyncToken }).ToListAsync(Ct);
                    var (uris, truncatedAt) = CollapseChanges(changes.Select(c => (c.Uri, c.SyncToken)), limit);
                    if (truncatedAt != null) newToken = truncatedAt.Value;
                    var existing = await _db.CalendarObjects.AsNoTracking()
                        .Where(o => o.CalendarId == cal.Calendar.Id && uris.Contains(o.Uri)).ToDictionaryAsync(o => o.Uri, Ct);
                    foreach (var uri in uris)
                    {
                        if (existing.TryGetValue(uri, out var o)) await AddResponseAsync(ms, _resolver.ObjectNode(cal, o), props);
                        else ms.AddStatus(cal.Href + DavPaths.Escape(uri), 404);
                    }
                    if (truncatedAt != null) ms.AddStatus(cal.Href, 507, DavXml.Error(D + "number-of-matches-within-limits"));
                }
                ms.SyncToken = new XElement(D + "sync-token", DavPropertyProvider.FormatSyncToken(newToken));
                break;
            }
            case AddressBookNode book:
            {
                var current = book.Book.SyncToken;
                if (since > current) throw new DavException(403, D + "valid-sync-token", "Sync token is from the future");
                long newToken = current;

                if (since == 0)
                {
                    var all = await _db.Cards.AsNoTracking().Where(c => c.AddressBookId == book.Book.Id).ToListAsync(Ct);
                    foreach (var c in all) await AddResponseAsync(ms, _resolver.CardNode(book, c), props);
                }
                else
                {
                    var changes = await _db.CardChanges.AsNoTracking()
                        .Where(c => c.AddressBookId == book.Book.Id && c.SyncToken > since)
                        .OrderBy(c => c.SyncToken).Select(c => new { c.Uri, c.SyncToken }).ToListAsync(Ct);
                    var (uris, truncatedAt) = CollapseChanges(changes.Select(c => (c.Uri, c.SyncToken)), limit);
                    if (truncatedAt != null) newToken = truncatedAt.Value;
                    var existing = await _db.Cards.AsNoTracking()
                        .Where(c => c.AddressBookId == book.Book.Id && uris.Contains(c.Uri)).ToDictionaryAsync(c => c.Uri, Ct);
                    foreach (var uri in uris)
                    {
                        if (existing.TryGetValue(uri, out var c)) await AddResponseAsync(ms, _resolver.CardNode(book, c), props);
                        else ms.AddStatus(book.Href + DavPaths.Escape(uri), 404);
                    }
                    if (truncatedAt != null) ms.AddStatus(book.Href, 507, DavXml.Error(D + "number-of-matches-within-limits"));
                }
                ms.SyncToken = new XElement(D + "sync-token", DavPropertyProvider.FormatSyncToken(newToken));
                break;
            }
            default:
                throw new DavException(403, D + "supported-report", "sync-collection is only supported on calendars and address books");
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    /// <summary>Reduces an ordered change log to distinct URIs, honouring an optional result limit.</summary>
    private static (List<string> Uris, long? TruncatedAt) CollapseChanges(IEnumerable<(string Uri, long Token)> changes, int limit)
    {
        var seen = new HashSet<string>();
        var ordered = new List<string>();
        long lastToken = 0;
        foreach (var (uri, token) in changes)
        {
            if (seen.Contains(uri))
            {
                lastToken = token;
                continue;
            }
            if (ordered.Count >= limit) return (ordered, lastToken);
            seen.Add(uri);
            ordered.Add(uri);
            lastToken = token;
        }
        return (ordered, null);
    }

    // ---------------------------------------------------------------- Principals (RFC 3744)

    private async Task PrincipalPropertySearchAsync(XElement root)
    {
        var allOf = string.Equals((string?)root.Attribute("test"), "allof", StringComparison.OrdinalIgnoreCase);
        var searches = root.Elements(D + "property-search").Select(ps => (
            Props: ps.Element(D + "prop")?.Elements().Select(e => e.Name).ToList() ?? [],
            Match: ps.Element(D + "match")?.Value.Trim() ?? "",
            MatchType: (string?)ps.Element(D + "match")?.Attribute("match-type") ?? "contains")).ToList();
        var props = RequestedProps(root);

        var users = await _db.Users.AsNoTracking().Where(u => u.IsEnabled).OrderBy(u => u.Username).ToListAsync(Ct);
        var ms = new MultiStatus();
        foreach (var u in users)
        {
            bool Test((List<XName> Props, string Match, string MatchType) s)
            {
                if (s.Match.Length == 0) return false;
                var tm = new TextMatch { Text = s.Match.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? s.Match[7..] : s.Match, MatchType = s.MatchType };
                foreach (var p in s.Props)
                {
                    if (p == D + "displayname" && (tm.Matches(u.DisplayName) || tm.Matches(u.Username))) return true;
                    if ((p == C + "calendar-user-address-set" || p == CS + "email-address-set") && u.Email != null && tm.Matches(u.Email)) return true;
                }
                return false;
            }

            var match = searches.Count > 0 && (allOf ? searches.All(Test) : searches.Any(Test));
            if (match) await AddResponseAsync(ms, new PrincipalNode(u, Paths.Principal(u.Username)), props);
            if (ms.Count >= 100) break;
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private async Task PrincipalSearchPropertySetAsync()
    {
        static XElement Item(XName name, string description) =>
            new(D + "principal-search-property",
                new XElement(D + "prop", new XElement(name)),
                new XElement(D + "description", new XAttribute(XNamespace.Xml + "lang", "en"), description));

        var root = new XElement(D + "principal-search-property-set",
            Item(D + "displayname", "Display name"),
            Item(C + "calendar-user-address-set", "Calendar user address"),
            Item(CS + "email-address-set", "Email address"));
        await DavXml.WriteAsync(Response, 200, root, Ct);
    }

    private async Task PrincipalMatchAsync(XElement root)
    {
        var props = RequestedProps(root);
        var ms = new MultiStatus();
        if (root.Element(D + "self") != null)
            await AddResponseAsync(ms, new PrincipalNode(_user, Paths.Principal(_user.Username)), props);
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    // ---------------------------------------------------------------- RFC 3253 expand-property

    private async Task ExpandPropertyAsync(DavNode node, XElement root)
    {
        var ms = new MultiStatus();
        ms.AddRaw(await ExpandResponseAsync(node, root.Elements(D + "property").ToList(), 0));
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private async Task<XElement> ExpandResponseAsync(DavNode node, List<XElement> properties, int level)
    {
        var results = new List<(XElement Prop, int Status)>();
        foreach (var p in properties)
        {
            var name = XName.Get((string?)p.Attribute("name") ?? "", (string?)p.Attribute("namespace") ?? "DAV:");
            var value = await _props.GetAsync(node, name, null, Ct);
            if (value == null)
            {
                results.Add((new XElement(name), 404));
                continue;
            }
            var nested = p.Elements(D + "property").ToList();
            if (nested.Count > 0 && level < 3)
            {
                var expanded = new XElement(name);
                foreach (var href in value.Elements(D + "href").Select(h => h.Value))
                {
                    var target = await ResolveHrefAsync(href);
                    expanded.Add(target == null
                        ? new XElement(D + "response", DavXml.Href(href), new XElement(D + "status", DavXml.StatusLine(404)))
                        : await ExpandResponseAsync(target, nested, level + 1));
                }
                value = expanded;
            }
            results.Add((value, 200));
        }

        var resp = new XElement(D + "response", DavXml.Href(node.Href));
        foreach (var g in Filter(results).GroupBy(r => r.Item2))
            resp.Add(new XElement(D + "propstat", new XElement(D + "prop", g.Select(x => x.Item1)),
                new XElement(D + "status", DavXml.StatusLine(g.Key))));
        return resp;
    }
}
