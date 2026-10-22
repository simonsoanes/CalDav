// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using Nullify.CalDav.Services;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

/// <summary>
/// Entry point for all requests below the DAV mount point. Implements WebDAV (RFC 4918),
/// WebDAV ACL subset (RFC 3744), CalDAV (RFC 4791), CardDAV (RFC 6352), extended MKCOL (RFC 5689)
/// and collection synchronization (RFC 6578).
/// </summary>
public sealed partial class DavHandler
{
    public const string Realm = "Nullify.CalDav";
    public const string DavCapabilities = "1, 3, access-control, calendar-access, calendar-auto-schedule, calendar-proxy, addressbook, extended-mkcol";
    private const string AllowedMethods = "OPTIONS, GET, HEAD, POST, DELETE, PROPFIND, PROPPATCH, PUT, REPORT, MKCOL, MKCALENDAR, MOVE";

    private static readonly string[] MutatingMethods = ["PUT", "DELETE", "MOVE", "MKCOL", "MKCALENDAR", "PROPPATCH", "POST"];

    private readonly HttpContext _http;
    private readonly AppDbContext _db;
    private readonly User _user;
    private readonly DavResolver _resolver;
    private readonly DavPropertyProvider _props;
    private readonly ILogger _log;
    private readonly bool _minimal;

    private HttpRequest Request => _http.Request;
    private HttpResponse Response => _http.Response;
    private CancellationToken Ct => _http.RequestAborted;
    private CalendarStore Calendars => _http.RequestServices.GetRequiredService<CalendarStore>();
    private AddressBookStore Books => _http.RequestServices.GetRequiredService<AddressBookStore>();
    private DavPaths Paths => _resolver.Paths;

    private DavHandler(HttpContext http, AppDbContext db, User user, ILogger log)
    {
        _http = http;
        _db = db;
        _user = user;
        _log = log;
        _resolver = new DavResolver(db, user, new DavPaths(http.Request.PathBase.Value ?? ""));
        _props = new DavPropertyProvider(_resolver, http.RequestServices);
        var prefer = http.Request.Headers["Prefer"].ToString();
        _minimal = prefer.Contains("return=minimal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(http.Request.Headers["Brief"].ToString(), "t", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Nullify.CalDav.Dav");
        http.Response.Headers["DAV"] = DavCapabilities;

        if (HttpMethods.IsOptions(http.Request.Method))
        {
            WriteOptions(http);
            return;
        }

        var db = services.GetRequiredService<AppDbContext>();
        var user = await AuthenticateAsync(http, db, services.GetRequiredService<DavCredentialValidator>());
        if (user == null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
            return;
        }

        var handler = new DavHandler(http, db, user, log);
        IDisposable? gate = null;
        try
        {
            if (MutatingMethods.Contains(http.Request.Method.ToUpperInvariant()))
                gate = await services.GetRequiredService<WriteGate>().EnterAsync(http.RequestAborted);
            await handler.DispatchAsync();
        }
        catch (DavException ex)
        {
            if (http.Response.HasStarted) throw;
            http.Response.Clear();
            http.Response.Headers["DAV"] = DavCapabilities;
            if (ex.Precondition != null)
            {
                await DavXml.WriteAsync(http.Response, ex.Status, DavXml.Error(ex.Precondition, ex.Detail), http.RequestAborted);
            }
            else
            {
                http.Response.StatusCode = ex.Status;
                if (ex.Status >= 400 && ex.Message.Length > 0)
                {
                    http.Response.ContentType = "text/plain; charset=utf-8";
                    await http.Response.WriteAsync(ex.Message, http.RequestAborted);
                }
            }
            log.LogDebug("{Method} {Path} -> {Status}: {Message}", http.Request.Method, http.Request.Path, ex.Status, ex.Message);
        }
        finally
        {
            gate?.Dispose();
        }
    }


    private static void WriteOptions(HttpContext http)
    {
        http.Response.Headers.Allow = AllowedMethods;
        http.Response.Headers["MS-Author-Via"] = "DAV";
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentLength = 0;
    }

    private static async Task<User?> AuthenticateAsync(HttpContext http, AppDbContext db, DavCredentialValidator validator)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch (FormatException) { return null; }
        var colon = decoded.IndexOf(':');
        if (colon <= 0) return null;
        var username = decoded[..colon];
        var password = decoded[(colon + 1)..];
        var clientKey = "dav:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return await validator.ValidateAsync(db, username, password, clientKey, http.RequestAborted);
    }

    private string[] Segments => DavPaths.SplitPath(Request.Path.Value ?? "/", decode: false);

    private async Task DispatchAsync()
    {
        var method = Request.Method.ToUpperInvariant();
        if (Segments is ["push", var registration])
        {
            if (method != "DELETE") throw new DavException(405, message: "Only DELETE is supported on push registrations");
            var deleted = await _db.PushRegistrations.Where(r => r.Id == registration && r.UserId == _user.Id).ExecuteDeleteAsync(Ct);
            Response.StatusCode = deleted > 0 ? 204 : 404;
            return;
        }
        switch (method)
        {
            case "GET": await GetAsync(head: false); break;
            case "HEAD": await GetAsync(head: true); break;
            case "PROPFIND": await PropFindAsync(); break;
            case "PROPPATCH": await PropPatchAsync(); break;
            case "PUT": await PutAsync(); break;
            case "DELETE": await DeleteAsync(); break;
            case "MKCALENDAR": await MkCalendarAsync(); break;
            case "MKCOL": await MkColAsync(); break;
            case "MOVE": await MoveAsync(); break;
            case "REPORT": await ReportAsync(); break;
            case "POST": await PostAsync(); break;
            default:
                Response.Headers.Allow = AllowedMethods;
                throw new DavException(StatusCodes.Status405MethodNotAllowed, message: "Method not supported");
        }
    }

    private async Task<ResolveResult> ResolveRequestAsync()
    {
        var res = await _resolver.ResolveAsync(Segments, Ct);
        if (res.Forbidden) throw new DavException(403, D + "need-privileges", "Access to another user's collections is not allowed");
        return res;
    }

    private async Task<DavNode> RequireNodeAsync()
    {
        var res = await ResolveRequestAsync();
        return res.Node ?? throw new DavException(404, message: "Not found");
    }

    private async Task<DavNode?> ResolveHrefAsync(string href)
    {
        var segs = Paths.ParseHref(href);
        if (segs == null) return null;
        var res = await _resolver.ResolveAsync(segs, Ct);
        return res.Forbidden ? null : res.Node;
    }

    // ---------------------------------------------------------------- GET / HEAD

    private async Task GetAsync(bool head)
    {
        var node = await RequireNodeAsync();
        string body;
        string contentType;
        string? etag = null;
        DateTime? modified = null;

        switch (node)
        {
            case CalendarObjectNode o:
                body = await _resolver.GetDataAsync(o, Ct);
                contentType = "text/calendar; charset=utf-8";
                etag = o.Object.ETag;
                modified = o.Object.ModifiedUtc;
                if (o.Object.ScheduleTag != null) Response.Headers["Schedule-Tag"] = o.Object.ScheduleTag;
                break;
            case CardNode c:
            {
                // RFC 6352 §5.1.1: honour the requested vCard version, defaulting to 3.0.
                var wants4 = Request.Headers.Accept.ToString().Contains("version=4.0", StringComparison.OrdinalIgnoreCase);
                body = VCardVersions.Convert(await _resolver.GetDataAsync(c, Ct), wants4 ? "4.0" : "3.0");
                contentType = wants4 ? "text/vcard; charset=utf-8; version=4.0" : "text/vcard; charset=utf-8";
                etag = c.Card.ETag;
                modified = c.Card.ModifiedUtc;
                break;
            }
            case CalendarNode cal:
                body = await Calendars.ExportAsync(cal.Calendar, Ct);
                contentType = "text/calendar; charset=utf-8";
                Response.Headers.ContentDisposition = $"inline; filename=\"{Names.Slugify(cal.DisplayName, "calendar")}.ics\"";
                break;
            case AddressBookNode book:
                body = await Books.ExportAsync(book.Book, Ct);
                contentType = "text/vcard; charset=utf-8";
                Response.Headers.ContentDisposition = $"inline; filename=\"{Names.Slugify(book.DisplayName, "contacts")}.vcf\"";
                break;
            default:
                body = "Nullify.CalDav - CalDAV/CardDAV server. Point your client at this URL.\n";
                contentType = "text/plain; charset=utf-8";
                break;
        }

        if (etag != null)
        {
            Response.Headers.ETag = etag;
            if (EtagListMatches(Request.Headers.IfNoneMatch.ToString(), etag))
            {
                Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }
        if (modified != null) Response.Headers.LastModified = DateTime.SpecifyKind(modified.Value, DateTimeKind.Utc).ToString("R");

        var bytes = Encoding.UTF8.GetBytes(body);
        Response.StatusCode = 200;
        Response.ContentType = contentType;
        Response.ContentLength = bytes.Length;
        if (!head) await Response.Body.WriteAsync(bytes, Ct);
    }

    // ---------------------------------------------------------------- Preconditions

    private static bool EtagListMatches(string header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") return true;
            var v = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (v == etag) return true;
        }
        return false;
    }

    private void CheckWritePreconditions(string? currentEtag)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifMatch) && (currentEtag == null || !EtagListMatches(ifMatch, currentEtag)))
            throw new DavException(StatusCodes.Status412PreconditionFailed, message: "If-Match precondition failed");
        var ifNone = Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifNone) && currentEtag != null && EtagListMatches(ifNone, currentEtag))
            throw new DavException(StatusCodes.Status412PreconditionFailed, message: "If-None-Match precondition failed");
    }

    private async Task<string> ReadBodyTextAsync(int maxBytes, XName sizePrecondition)
    {
        if (Request.ContentLength > maxBytes) throw new DavException(403, sizePrecondition, "Resource too large");
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(buffer, Ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes) throw new DavException(403, sizePrecondition, "Resource too large");
        }
        var bytes = ms.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    // ---------------------------------------------------------------- PUT

    private async Task PutAsync()
    {
        var res = await ResolveRequestAsync();
        switch (res.Node, res.Parent)
        {
            case (CalendarObjectNode o, _):
                await PutCalendarObjectAsync(o.Calendar, o.Object.Uri, o.Object);
                return;
            case (null, CalendarNode cal) when res.Name != null:
                await PutCalendarObjectAsync(cal, res.Name, null);
                return;
            case (CardNode c, _):
                await PutCardAsync(c.Book, c.Card.Uri, c.Card);
                return;
            case (null, AddressBookNode book) when res.Name != null:
                await PutCardAsync(book, res.Name, null);
                return;
            case ({ IsCollection: true }, _):
                throw new DavException(405, message: "Cannot PUT to a collection");
            default:
                throw new DavException(409, message: "Parent collection does not exist");
        }
    }

    private async Task PutCalendarObjectAsync(CalendarNode cal, string name, CalendarObject? existing)
    {
        if (!cal.CanWrite) throw new DavException(403, D + "need-privileges", "Calendar is read-only");
        if (!Names.IsValidSegment(name)) throw new DavException(400, message: "Invalid resource name");
        CheckWritePreconditions(existing?.ETag);
        var scheduleTagMatch = Request.Headers["If-Schedule-Tag-Match"].ToString();
        if (!string.IsNullOrWhiteSpace(scheduleTagMatch) && (existing?.ScheduleTag == null || !EtagListMatches(scheduleTagMatch, existing.ScheduleTag)))
            throw new DavException(StatusCodes.Status412PreconditionFailed, message: "If-Schedule-Tag-Match precondition failed");

        var data = await ReadBodyTextAsync(CalendarStore.MaxResourceSize, C + "max-resource-size");
        CalendarObjectInfo info;
        try { info = CalendarObjectInfo.Analyze(data); }
        catch (InvalidObjectException ex) { throw new DavException(403, C + "valid-calendar-data", ex.Message); }

        if (!cal.Calendar.ComponentList.Contains(info.ComponentType))
            throw new DavException(403, C + "supported-calendar-component", $"{info.ComponentType} is not supported by this calendar");
        if (existing != null && existing.Uid != info.Uid && !string.IsNullOrEmpty(existing.Uid))
            throw new DavException(403, C + "no-uid-conflict", "UID of an existing resource cannot change",
                DavXml.Href(_resolver.ObjectNode(cal, existing).Href));

        // Implicit scheduling may add SCHEDULE-STATUS parameters, in which case the client must re-fetch (no ETag).
        var scheduling = _http.RequestServices.GetRequiredService<SchedulingService>();
        var stored = await scheduling.OnPutAsync(cal.Calendar, cal.Calendar.Owner, existing?.Data, data, Ct);
        if (!ReferenceEquals(stored, data) && stored != data) info = CalendarObjectInfo.Analyze(stored);

        var store = Calendars;
        CalendarObject saved;
        try { saved = await store.PutAsync(cal.Calendar, name, stored, info, existing, ct: Ct); }
        catch (UidConflictException ex)
        {
            throw new DavException(403, C + "no-uid-conflict", "UID already in use", DavXml.Href(cal.Href + DavPaths.Escape(ex.ExistingUri)));
        }
        catch (QuotaExceededException)
        {
            throw new DavException(StatusCodes.Status507InsufficientStorage, D + "quota-not-exceeded", "Storage quota exceeded");
        }

        if (stored == data) Response.Headers.ETag = saved.ETag;
        if (saved.ScheduleTag != null) Response.Headers["Schedule-Tag"] = saved.ScheduleTag;
        Response.StatusCode = existing == null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    private async Task PutCardAsync(AddressBookNode book, string name, Card? existing)
    {
        if (!book.CanWrite) throw new DavException(403, D + "need-privileges", "Address book is read-only");
        if (!Names.IsValidSegment(name)) throw new DavException(400, message: "Invalid resource name");
        CheckWritePreconditions(existing?.ETag);

        var data = await ReadBodyTextAsync(AddressBookStore.MaxResourceSize, CR + "max-resource-size");
        CardInfo info;
        try { info = CardInfo.Analyze(data, existing?.Uid ?? Path.GetFileNameWithoutExtension(name)); }
        catch (InvalidObjectException ex) { throw new DavException(403, CR + "valid-address-data", ex.Message); }

        var store = Books;
        Card saved;
        try { saved = await store.PutAsync(book.Book, name, data, info, existing, Ct); }
        catch (UidConflictException ex)
        {
            throw new DavException(403, CR + "no-uid-conflict", "UID already in use", DavXml.Href(book.Href + DavPaths.Escape(ex.ExistingUri)));
        }
        catch (QuotaExceededException)
        {
            throw new DavException(StatusCodes.Status507InsufficientStorage, D + "quota-not-exceeded", "Storage quota exceeded");
        }

        Response.Headers.ETag = saved.ETag;
        Response.StatusCode = existing == null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    // ---------------------------------------------------------------- DELETE

    private async Task DeleteAsync()
    {
        var node = await RequireNodeAsync();
        switch (node)
        {
            case CalendarObjectNode o:
                if (!o.Calendar.CanDelete) throw new DavException(403, D + "need-privileges");
                CheckWritePreconditions(o.Object.ETag);
                if (!o.Calendar.IsInbox)
                    await _http.RequestServices.GetRequiredService<SchedulingService>()
                        .OnDeleteAsync(o.Calendar.Calendar, o.Calendar.Calendar.Owner, await _resolver.GetDataAsync(o, Ct), Ct);
                await Calendars.DeleteObjectAsync(o.Calendar.Calendar, o.Object, Ct);
                break;
            case CardNode c:
                if (!c.Book.CanWrite) throw new DavException(403, D + "need-privileges");
                CheckWritePreconditions(c.Card.ETag);
                await Books.DeleteCardAsync(c.Book.Book, c.Card, Ct);
                break;
            case CalendarNode cal:
                if (cal.IsOwner)
                {
                    if (cal.Calendar.Kind is CalendarKind.Inbox or CalendarKind.Birthdays)
                        throw new DavException(403, D + "need-privileges", "This calendar is managed by the server");
                    await Calendars.DeleteAsync(cal.Calendar, Ct);
                }
                else if (cal.Share is { ShareeId: not null }) await _db.CalendarShares.Where(s => s.Id == cal.Share.Id).ExecuteDeleteAsync(Ct);
                else throw new DavException(403, D + "need-privileges", "Group and delegated calendars cannot be removed by members");
                break;
            case AddressBookNode book:
                if (book.IsOwner)
                {
                    if (book.Book.Kind == AddressBookKind.Directory)
                        throw new DavException(403, D + "need-privileges", "The directory is managed by the server");
                    await Books.DeleteAsync(book.Book, Ct);
                }
                else if (book.Share is { ShareeId: not null }) await _db.AddressBookShares.Where(s => s.Id == book.Share.Id).ExecuteDeleteAsync(Ct);
                else throw new DavException(403, D + "need-privileges", "Group address books cannot be removed by members");
                break;
            default:
                throw new DavException(403, D + "need-privileges", "This resource cannot be deleted");
        }
        Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ---------------------------------------------------------------- MOVE

    private async Task MoveAsync()
    {
        var node = await RequireNodeAsync();
        var destination = Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(destination)) throw new DavException(400, message: "Missing Destination header");
        var segs = Paths.ParseHref(destination) ?? throw new DavException(502, message: "Destination is not on this server");
        var dest = await _resolver.ResolveAsync(segs, Ct);
        if (dest.Forbidden) throw new DavException(403, D + "need-privileges");
        var overwrite = !string.Equals(Request.Headers["Overwrite"].ToString().Trim(), "F", StringComparison.OrdinalIgnoreCase);

        switch (node)
        {
            case CalendarObjectNode o:
            {
                var target = dest.Node is CalendarObjectNode d ? d.Calendar : dest.Parent as CalendarNode;
                if (target == null || dest.Name == null) throw new DavException(409, message: "Destination collection does not exist");
                if (!o.Calendar.CanWrite || !target.CanWrite) throw new DavException(403, D + "need-privileges");
                var existing = (dest.Node as CalendarObjectNode)?.Object;
                if (existing?.Id == o.Object.Id) throw new DavException(403, message: "Source and destination are the same");
                if (existing != null && !overwrite) throw new DavException(412, message: "Destination exists");
                if (!target.Calendar.ComponentList.Contains(o.Object.ComponentType))
                    throw new DavException(403, C + "supported-calendar-component");
                if (!Names.IsValidSegment(dest.Name)) throw new DavException(400, message: "Invalid resource name");
                try { await Calendars.MoveObjectAsync(o.Calendar.Calendar, o.Object, target.Calendar, dest.Name, existing, Ct); }
                catch (UidConflictException) { throw new DavException(403, C + "no-uid-conflict"); }
                Response.StatusCode = existing == null ? 201 : 204;
                return;
            }
            case CardNode c:
            {
                var target = dest.Node is CardNode d ? d.Book : dest.Parent as AddressBookNode;
                if (target == null || dest.Name == null) throw new DavException(409, message: "Destination collection does not exist");
                if (!c.Book.CanWrite || !target.CanWrite) throw new DavException(403, D + "need-privileges");
                var existing = (dest.Node as CardNode)?.Card;
                if (existing?.Id == c.Card.Id) throw new DavException(403, message: "Source and destination are the same");
                if (existing != null && !overwrite) throw new DavException(412, message: "Destination exists");
                if (!Names.IsValidSegment(dest.Name)) throw new DavException(400, message: "Invalid resource name");
                try { await Books.MoveCardAsync(c.Book.Book, c.Card, target.Book, dest.Name, existing, Ct); }
                catch (UidConflictException) { throw new DavException(403, CR + "no-uid-conflict"); }
                Response.StatusCode = existing == null ? 201 : 204;
                return;
            }
            default:
                throw new DavException(403, message: "Only calendar objects and contacts can be moved");
        }
    }

    // ---------------------------------------------------------------- PROPFIND

    private int Depth(int @default)
    {
        var h = Request.Headers["Depth"].ToString().Trim();
        return h switch
        {
            "0" => 0,
            "1" => 1,
            "infinity" or "Infinity" => 1, // infinite depth is served as depth 1
            _ => @default,
        };
    }

    private async Task PropFindAsync()
    {
        var depth = Depth(1);
        var doc = await DavXml.ReadBodyAsync(Request, Ct);
        var node = await RequireNodeAsync();

        var root = doc?.Root;
        if (root != null && root.Name != D + "propfind") throw new DavException(400, message: "Expected DAV:propfind");
        var propEl = root?.Element(D + "prop");
        var propName = root?.Element(D + "propname") != null;
        var include = root?.Element(D + "include")?.Elements().ToList();

        var nodes = new List<DavNode> { node };
        if (depth > 0 && node.IsCollection) nodes.AddRange(await _resolver.ChildrenAsync(node, Ct));

        var ms = new MultiStatus();
        foreach (var n in nodes)
        {
            List<(XElement, int)> values;
            if (propName) values = _props.AllPropNames(n).Select(x => (new XElement(x), 200)).ToList();
            else if (propEl != null) values = await _props.GetManyAsync(n, propEl.Elements(), Ct);
            else values = await _props.GetAllAsync(n, include, Ct);
            ms.AddPropStat(n.Href, Filter(values));
        }
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private IEnumerable<(XElement, int)> Filter(List<(XElement Prop, int Status)> values) =>
        _minimal ? values.Where(v => v.Status == 200) : values;

    // ---------------------------------------------------------------- PROPPATCH / MKCALENDAR / MKCOL

    private async Task PropPatchAsync()
    {
        var node = await RequireNodeAsync();
        var doc = await DavXml.ReadBodyAsync(Request, Ct);
        if (doc?.Root?.Name != D + "propertyupdate") throw new DavException(400, message: "Expected DAV:propertyupdate");

        var ops = doc.Root.Elements()
            .Where(e => e.Name == D + "set" || e.Name == D + "remove")
            .SelectMany(e => (e.Element(D + "prop")?.Elements() ?? []).Select(p => (Prop: p, Remove: e.Name == D + "remove")))
            .ToList();

        var results = new List<(XElement Prop, int Status)>();
        foreach (var (prop, remove) in ops)
        {
            var status = node switch
            {
                CalendarNode { IsOwner: false, Share: null or { ShareeId: null } } => 403,
                AddressBookNode { IsOwner: false, Share: null or { ShareeId: null } } => 403,
                CalendarNode cal => ApplyCalendarProperty(cal.Calendar, cal.Share, prop, remove, creating: false),
                AddressBookNode book => ApplyAddressBookProperty(book.Book, book.Share, prop, remove),
                _ => 403,
            };
            results.Add((new XElement(prop.Name), status));
        }

        var failed = results.Any(r => r.Status != 200);
        if (failed)
        {
            results = results.Select(r => r.Status == 200 ? (r.Prop, 424) : r).ToList();
            _db.ChangeTracker.Clear();
        }
        else
        {
            if (node is CalendarNode c && c.IsOwner) c.Calendar.ModifiedUtc = DateTime.UtcNow;
            if (node is AddressBookNode b && b.IsOwner) b.Book.ModifiedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(Ct);
        }

        var ms = new MultiStatus();
        ms.AddPropStat(node.Href, results);
        await DavXml.WriteAsync(Response, 207, ms.ToXml(), Ct);
    }

    private static int ApplyCalendarProperty(Calendar cal, CalendarShare? share, XElement prop, bool remove, bool creating)
    {
        var name = prop.Name;
        var value = remove ? null : prop.Value.Trim();

        if (share != null)
        {
            // Sharees may only personalise the name and colour of their view of the calendar.
            if (name == D + "displayname") { share.DisplayName = string.IsNullOrEmpty(value) ? null : value; return 200; }
            if (name == A + "calendar-color") { share.Color = string.IsNullOrEmpty(value) ? null : value; return 200; }
            if (name == A + "calendar-order") return 200;
            return 403;
        }

        if (name == D + "displayname") { cal.DisplayName = string.IsNullOrEmpty(value) ? cal.Uri : value; return 200; }
        if (name == C + "calendar-description") { cal.Description = value; return 200; }
        if (name == A + "calendar-color") { cal.Color = string.IsNullOrEmpty(value) ? null : value; return 200; }
        if (name == A + "calendar-order")
        {
            cal.SortOrder = int.TryParse(value, out var order) ? order : 0;
            return 200;
        }
        if (name == C + "calendar-timezone")
        {
            if (value == null) { cal.TimeZone = null; return 200; }
            try
            {
                var root = CalendarObjectInfo.ParseCalendar(prop.Value);
                if (!root.ChildrenNamed("VTIMEZONE").Any()) return 403;
            }
            catch (InvalidObjectException) { return 403; }
            cal.TimeZone = prop.Value;
            return 200;
        }
        if (name == C + "supported-calendar-component-set" && creating)
        {
            var comps = prop.Elements(C + "comp").Select(e => ((string?)e.Attribute("name") ?? "").ToUpperInvariant())
                .Where(CalendarStore.SupportedComponents.Contains).Distinct().ToList();
            if (comps.Count == 0) return 403;
            cal.Components = string.Join(',', comps);
            return 200;
        }
        if (name == D + "resourcetype" && creating) return 200;
        return 403;
    }

    private static int ApplyAddressBookProperty(AddressBook book, AddressBookShare? share, XElement prop, bool remove)
    {
        var value = remove ? null : prop.Value.Trim();
        if (share != null)
        {
            if (prop.Name == D + "displayname") { share.DisplayName = string.IsNullOrEmpty(value) ? null : value; return 200; }
            return 403;
        }
        if (prop.Name == D + "displayname") { book.DisplayName = string.IsNullOrEmpty(value) ? book.Uri : value; return 200; }
        if (prop.Name == CR + "addressbook-description") { book.Description = value; return 200; }
        if (prop.Name == D + "resourcetype") return 200;
        return 403;
    }

    private async Task MkCalendarAsync()
    {
        var res = await ResolveRequestAsync();
        if (res.Node != null) throw new DavException(405, message: "Resource already exists");
        if (res.Parent is not CalendarHomeNode { Access: NodeAccess.Owner } home || res.Name == null)
            throw new DavException(403, C + "calendar-collection-location-ok", "Calendars can only be created in your own calendar home");
        var doc = await DavXml.ReadBodyAsync(Request, Ct);
        var props = doc?.Root?.Elements(D + "set").SelectMany(s => s.Element(D + "prop")?.Elements() ?? []).ToList() ?? [];
        await CreateCalendarAsync(home, res.Name, props, lenient: true);
    }

    private async Task CreateCalendarAsync(CalendarHomeNode home, string name, List<XElement> props, bool lenient)
    {
        if (!Names.IsValidSegment(name)) throw new DavException(400, message: "Invalid collection name");
        var store = Calendars;
        if (await store.HomeUriTakenAsync(home.Owner.Id, name, Ct)) throw new DavException(405, message: "Resource already exists");

        var cal = new Calendar { OwnerId = home.Owner.Id, Uri = name, DisplayName = name };
        var results = props.Select(p => (Prop: new XElement(p.Name), Status: ApplyCalendarProperty(cal, null, p, false, creating: true))).ToList();
        if (!lenient && results.Any(r => r.Status != 200))
        {
            var resp = new XElement(D + "mkcol-response",
                results.Select(r => new XElement(D + "propstat", new XElement(D + "prop", r.Prop),
                    new XElement(D + "status", DavXml.StatusLine(r.Status == 200 ? 424 : r.Status)))));
            await DavXml.WriteAsync(Response, 403, resp, Ct);
            return;
        }
        _db.Calendars.Add(cal);
        await _db.SaveChangesAsync(Ct);
        Response.StatusCode = StatusCodes.Status201Created;
        Response.Headers.Location = Paths.Calendar(home.Owner.Username, cal.Uri);
    }

    private async Task MkColAsync()
    {
        var res = await ResolveRequestAsync();
        if (res.Node != null) throw new DavException(405, message: "Resource already exists");
        if (res.Name == null) throw new DavException(409, message: "Parent does not exist");

        var doc = await DavXml.ReadBodyAsync(Request, Ct);
        if (doc?.Root != null && doc.Root.Name != D + "mkcol") throw new DavException(415, message: "Unsupported MKCOL body");
        var props = doc?.Root?.Elements(D + "set").SelectMany(s => s.Element(D + "prop")?.Elements() ?? []).ToList() ?? [];
        var resourceType = props.FirstOrDefault(p => p.Name == D + "resourcetype");
        var isCalendar = resourceType?.Element(C + "calendar") != null;
        var isAddressBook = resourceType?.Element(CR + "addressbook") != null;

        if (res.Parent is CalendarHomeNode { Access: NodeAccess.Owner } home && isCalendar)
        {
            await CreateCalendarAsync(home, res.Name, props, lenient: false);
            return;
        }

        if (res.Parent is AddressBookHomeNode bookHome && (isAddressBook || resourceType == null))
        {
            if (!Names.IsValidSegment(res.Name)) throw new DavException(400, message: "Invalid collection name");
            var store = Books;
            if (await store.HomeUriTakenAsync(bookHome.Owner.Id, res.Name, Ct)) throw new DavException(405);
            var book = new AddressBook { OwnerId = bookHome.Owner.Id, Uri = res.Name, DisplayName = res.Name };
            var results = props.Select(p => (Prop: new XElement(p.Name), Status: ApplyAddressBookProperty(book, null, p, false))).ToList();
            if (results.Any(r => r.Status != 200))
            {
                var resp = new XElement(D + "mkcol-response",
                    results.Select(r => new XElement(D + "propstat", new XElement(D + "prop", r.Prop),
                        new XElement(D + "status", DavXml.StatusLine(r.Status == 200 ? 424 : r.Status)))));
                await DavXml.WriteAsync(Response, 403, resp, Ct);
                return;
            }
            _db.AddressBooks.Add(book);
            await _db.SaveChangesAsync(Ct);
            Response.StatusCode = StatusCodes.Status201Created;
            Response.Headers.Location = Paths.AddressBook(bookHome.Owner.Username, book.Uri);
            return;
        }

        throw new DavException(403, D + "valid-resourcetype", "Only calendar and address book collections can be created here");
    }
}
