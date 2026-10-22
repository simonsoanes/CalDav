// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using Calendar = Nullify.CalDav.Data.Calendar;

namespace Nullify.CalDav.Services;

public sealed class RemoteDavException(string message) : Exception(message);

/// <summary>A collection found on a remote CalDAV/CardDAV server.</summary>
public sealed record RemoteCollection(string Kind, Uri Url, string DisplayName, string? Description, string? Color, string? Components, bool ReadOnly);

/// <summary>
/// Client for reading another CalDAV/CardDAV server (Baïkal, Nextcloud, Radicale, iCloud, Fastmail, ...):
/// RFC 6764 discovery, home-set lookup, collection listing and resource download (multiget with GET fallback).
/// Supports Basic and Digest authentication.
/// </summary>
public sealed class RemoteDavClient : IDisposable
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";

    private readonly HttpClient _http;

    public RemoteDavClient(string username, string password, bool allowPrivate)
    {
        var handler = OutboundHttp.CreateHandler(allowPrivate);
        handler.Credentials = new NetworkCredential(username, password);
        handler.PreAuthenticate = true;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Nullify.CalDav-Migration/1.0");
    }

    public void Dispose() => _http.Dispose();

    public static Uri NormalizeServerUrl(string input)
    {
        var s = input.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new RemoteDavException("Enter the server address, e.g. https://dav.example.com/ or https://example.com/baikal/dav.php/");
        return uri;
    }

    // ------------------------------------------------------------------ HTTP

    private sealed record DavResponse(Uri Href, Dictionary<XName, XElement> Props);

    private async Task<(HttpStatusCode Status, Uri FinalUrl, byte[] Body)> SendAsync(string method, Uri url, string? body, int depth, CancellationToken ct)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
            }
            if (method is "PROPFIND" or "REPORT") request.Headers.TryAddWithoutValidation("Depth", depth.ToString());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var status = response.StatusCode;
            if ((int)status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (status == HttpStatusCode.Unauthorized) throw new RemoteDavException("The server rejected the username or password.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 50 * 1024 * 1024) throw new RemoteDavException("Response too large.");
            return (status, url, bytes);
        }
        throw new RemoteDavException("Too many redirects.");
    }

    private static XDocument ParseXml(byte[] body)
    {
        using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static string PropfindBody(IEnumerable<XName> props) =>
        new XElement(D + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cr", CR.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", CS.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
            new XElement(D + "prop", props.Select(p => new XElement(p)))).ToString(SaveOptions.DisableFormatting);

    private async Task<(Uri Url, List<DavResponse> Responses)> PropfindAsync(Uri url, int depth, IEnumerable<XName> props, CancellationToken ct)
    {
        var (status, final, body) = await SendAsync("PROPFIND", url, PropfindBody(props), depth, ct);
        if (status != HttpStatusCode.MultiStatus) throw new RemoteDavException($"PROPFIND {final} returned {(int)status}.");
        return (final, ParseMultistatus(final, body));
    }

    private static List<DavResponse> ParseMultistatus(Uri baseUrl, byte[] body)
    {
        XDocument doc;
        try { doc = ParseXml(body); }
        catch (XmlException ex) { throw new RemoteDavException("Invalid XML from server: " + ex.Message); }
        var result = new List<DavResponse>();
        foreach (var resp in doc.Root?.Elements(D + "response") ?? [])
        {
            var href = resp.Element(D + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;
            var props = new Dictionary<XName, XElement>();
            foreach (var ps in resp.Elements(D + "propstat"))
            {
                var st = ps.Element(D + "status")?.Value ?? "";
                if (!st.Contains(" 200")) continue;
                foreach (var p in ps.Element(D + "prop")?.Elements() ?? []) props[p.Name] = p;
            }
            result.Add(new DavResponse(new Uri(baseUrl, href), props));
        }
        return result;
    }

    private static Uri? HrefOf(DavResponse r, XName prop, Uri baseUrl) =>
        r.Props.TryGetValue(prop, out var e) && e.Element(D + "href")?.Value.Trim() is { Length: > 0 } h ? new Uri(baseUrl, h) : null;

    private static List<Uri> HrefsOf(DavResponse r, XName prop, Uri baseUrl) =>
        r.Props.TryGetValue(prop, out var e) ? e.Elements(D + "href").Select(h => new Uri(baseUrl, h.Value.Trim())).ToList() : [];

    private static bool SameResource(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 ||
        a.AbsolutePath.TrimEnd('/') == b.AbsolutePath.TrimEnd('/') && a.Authority == b.Authority;

    // ------------------------------------------------------------------ Discovery

    private static readonly XName[] PrincipalProps = [D + "current-user-principal", C + "calendar-home-set", CR + "addressbook-home-set", D + "resourcetype"];

    private static readonly XName[] CollectionProps =
    [
        D + "resourcetype", D + "displayname", C + "calendar-description", CR + "addressbook-description",
        A + "calendar-color", C + "supported-calendar-component-set", D + "current-user-privilege-set",
    ];

    /// <summary>Finds every calendar and address book the account can see.</summary>
    public async Task<List<RemoteCollection>> DiscoverAsync(Uri server, CancellationToken ct)
    {
        var homes = new List<Uri>();
        var starts = new List<Uri> { server };
        if (server.AbsolutePath is "" or "/")
        {
            starts.Add(new Uri(server, "/.well-known/caldav"));
            starts.Add(new Uri(server, "/.well-known/carddav"));
        }

        Exception? lastError = null;
        foreach (var start in starts)
        {
            try
            {
                var (url, responses) = await PropfindAsync(start, 0, PrincipalProps, ct);
                var self = responses.FirstOrDefault();
                if (self == null) continue;
                homes.AddRange(HrefsOf(self, C + "calendar-home-set", url));
                homes.AddRange(HrefsOf(self, CR + "addressbook-home-set", url));
                if (HrefOf(self, D + "current-user-principal", url) is { } principal)
                {
                    var (purl, presp) = await PropfindAsync(principal, 0, PrincipalProps, ct);
                    if (presp.FirstOrDefault() is { } p)
                    {
                        homes.AddRange(HrefsOf(p, C + "calendar-home-set", purl));
                        homes.AddRange(HrefsOf(p, CR + "addressbook-home-set", purl));
                    }
                }
                if (homes.Count == 0) homes.Add(url); // the address may itself be a home or a collection
                break;
            }
            catch (RemoteDavException ex) when (!ex.Message.StartsWith("The server rejected", StringComparison.Ordinal))
            {
                lastError = ex;
            }
            catch (HttpRequestException ex)
            {
                lastError = new RemoteDavException("Could not connect: " + ex.Message);
            }
        }
        if (homes.Count == 0) throw lastError as RemoteDavException ?? new RemoteDavException("No CalDAV or CardDAV service found at this address.");

        var found = new Dictionary<string, RemoteCollection>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes.DistinctBy(h => h.AbsoluteUri))
        {
            List<DavResponse> responses;
            Uri baseUrl;
            try { (baseUrl, responses) = await PropfindAsync(home, 1, CollectionProps, ct); }
            catch (RemoteDavException) when (homes.Count > 1) { continue; }

            foreach (var r in responses)
            {
                if (!r.Props.TryGetValue(D + "resourcetype", out var rt)) continue;
                var isCalendar = rt.Element(C + "calendar") != null;
                var isBook = rt.Element(CR + "addressbook") != null;
                // Skip scheduling inboxes/outboxes and server-side subscriptions (they are not the user's data).
                if (!isCalendar && !isBook || rt.Element(CS + "subscribed") != null) continue;
                var name = r.Props.TryGetValue(D + "displayname", out var dn) && dn.Value.Trim().Length > 0
                    ? dn.Value.Trim()
                    : Uri.UnescapeDataString(r.Href.AbsolutePath.TrimEnd('/').Split('/').Last());
                var description = (r.Props.GetValueOrDefault(C + "calendar-description") ?? r.Props.GetValueOrDefault(CR + "addressbook-description"))?.Value.Trim();
                var color = r.Props.GetValueOrDefault(A + "calendar-color")?.Value.Trim();
                var comps = r.Props.GetValueOrDefault(C + "supported-calendar-component-set")?.Elements(C + "comp")
                    .Select(e => ((string?)e.Attribute("name") ?? "").ToUpperInvariant())
                    .Where(CalendarStore.SupportedComponents.Contains).ToList();
                // Read-only collections (shared with the user, generated or subscribed) are offered but not pre-selected.
                var readOnly = r.Props.TryGetValue(D + "current-user-privilege-set", out var privs) &&
                               !privs.Descendants().Any(e => e.Name == D + "all" || e.Name == D + "write" || e.Name == D + "write-content" || e.Name == D + "bind");
                found[r.Href.AbsoluteUri] = new RemoteCollection(isCalendar ? "calendar" : "addressbook", r.Href, name,
                    string.IsNullOrEmpty(description) ? null : description,
                    string.IsNullOrEmpty(color) ? null : color,
                    comps is { Count: > 0 } ? string.Join(',', comps) : null,
                    readOnly);
            }
        }
        if (found.Count == 0) throw new RemoteDavException("Connected, but no calendars or address books were found for this account.");
        return found.Values.OrderBy(c => c.Kind).ThenBy(c => c.DisplayName).ToList();
    }

    // ------------------------------------------------------------------ Download

    /// <summary>Lists member resources of a collection.</summary>
    public async Task<List<Uri>> ListAsync(Uri collection, CancellationToken ct)
    {
        var (baseUrl, responses) = await PropfindAsync(collection, 1, [D + "resourcetype", D + "getetag"], ct);
        return responses
            .Where(r => !SameResource(r.Href, collection) && !SameResource(r.Href, baseUrl))
            .Where(r => !r.Props.TryGetValue(D + "resourcetype", out var rt) || rt.Element(D + "collection") == null)
            .Select(r => r.Href).DistinctBy(u => u.AbsoluteUri).ToList();
    }

    /// <summary>Downloads resources (href → data), using multiget and falling back to individual GETs.</summary>
    public async Task<Dictionary<Uri, string>> FetchAsync(Uri collection, string kind, IReadOnlyList<Uri> hrefs, CancellationToken ct)
    {
        var result = new Dictionary<Uri, string>();
        var ns = kind == "calendar" ? C : CR;
        var dataName = kind == "calendar" ? C + "calendar-data" : CR + "address-data";
        var report = new XElement(ns + (kind == "calendar" ? "calendar-multiget" : "addressbook-multiget"),
            new XAttribute(XNamespace.Xmlns + "d", D.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", ns.NamespaceName),
            new XElement(D + "prop", new XElement(D + "getetag"), new XElement(dataName)),
            hrefs.Select(h => new XElement(D + "href", h.AbsolutePath)));

        var multigetOk = false;
        try
        {
            var (status, final, body) = await SendAsync("REPORT", collection, report.ToString(SaveOptions.DisableFormatting), 1, ct);
            if (status == HttpStatusCode.MultiStatus)
            {
                foreach (var r in ParseMultistatus(final, body))
                    if (r.Props.TryGetValue(dataName, out var data) && data.Value.Trim().Length > 0)
                        result[hrefs.FirstOrDefault(h => SameResource(h, r.Href)) ?? r.Href] = data.Value;
                multigetOk = true;
            }
        }
        catch (RemoteDavException) when (!multigetOk) { }

        foreach (var href in hrefs.Where(h => !result.Keys.Any(k => SameResource(k, h))))
        {
            var (status, _, body) = await SendAsync("GET", href, null, 0, ct);
            if (status == HttpStatusCode.OK) result[href] = Encoding.UTF8.GetString(body);
        }
        return result;
    }
}

/// <summary>Discovers remote collections and imports them into the local account.</summary>
public sealed class MigrationService(IServiceScopeFactory scopes, IDataProtectionProvider protection, SettingsService settings,
    WriteGate gate, ILogger<MigrationService> log)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nullify.CalDav.Migration");
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private const int BatchSize = 50;

    public ChannelReader<int> Queue => _queue.Reader;

    public bool AllowPrivate(User user) => user.IsAdmin || settings.Current.SubscriptionsAllowPrivateNetworks;

    /// <summary>
    /// Whether a connection may go to loopback/private addresses. Allowed server-wide by the admin setting, for
    /// administrators, and for a source an administrator set up as long as it still points at the same server.
    /// </summary>
    public static bool PrivateAllowed(bool globalSetting, bool actorIsAdmin, MigrationSource? existing, string serverUrl) =>
        globalSetting || actorIsAdmin || (existing is { AllowPrivateNetworks: true } && existing.ServerUrl == serverUrl);

    /// <summary>Connects to the server and records the collections found for the signed-in user. Returns the source id.</summary>
    public Task<int> ConnectAsync(AppDbContext db, User user, string serverUrl, string username, string password, int? existingId, CancellationToken ct) =>
        ConnectAsync(db, user, user, serverUrl, username, password, existingId, ct);

    /// <summary>
    /// Connects to the server and records the collections found, importing into <paramref name="target"/>'s account.
    /// Only administrators may act for another user (enforced by the caller). Returns the source id.
    /// </summary>
    public async Task<int> ConnectAsync(AppDbContext db, User actor, User target, string serverUrl, string username, string password,
        int? existingId, CancellationToken ct)
    {
        if (actor.Id != target.Id && !actor.IsAdmin) throw new RemoteDavException("Only administrators can import into another account.");
        var server = RemoteDavClient.NormalizeServerUrl(serverUrl);
        var existing = existingId == null ? null
            : await db.MigrationSources.Include(s => s.Collections).FirstOrDefaultAsync(s => s.Id == existingId && s.UserId == target.Id, ct);
        var allowPrivate = PrivateAllowed(settings.Current.SubscriptionsAllowPrivateNetworks, actor.IsAdmin, existing, server.ToString());

        List<RemoteCollection> collections;
        using (var client = new RemoteDavClient(username, password, allowPrivate))
            collections = await client.DiscoverAsync(server, ct);

        using var _ = await gate.EnterAsync(ct);
        var source = existing;
        if (source == null)
        {
            source = new MigrationSource { UserId = target.Id, CreatedByUserId = actor.Id };
            db.MigrationSources.Add(source);
        }
        // An administrator's network permission sticks to the source (so the user can re-run it), but only while
        // it keeps pointing at the server the administrator approved.
        source.AllowPrivateNetworks = actor.IsAdmin || (source.AllowPrivateNetworks && source.ServerUrl == server.ToString());
        if (actor.IsAdmin) source.CreatedByUserId = actor.Id;
        source.ServerUrl = server.ToString();
        source.Username = username;
        source.ProtectedPassword = _protector.Protect(password);
        source.Message = $"Found {collections.Count(c => c.Kind == "calendar")} calendar(s) and {collections.Count(c => c.Kind == "addressbook")} address book(s).";
        if (source.Status != MigrationStatus.Running && source.Status != MigrationStatus.Queued) source.Status = MigrationStatus.Ready;
        foreach (var rc in collections)
        {
            var row = source.Collections.FirstOrDefault(c => c.RemoteUrl == rc.Url.AbsoluteUri);
            if (row == null) source.Collections.Add(row = new MigrationCollection { RemoteUrl = rc.Url.AbsoluteUri, Kind = rc.Kind, Selected = !rc.ReadOnly });
            row.ReadOnly = rc.ReadOnly;
            row.DisplayName = rc.DisplayName;
            row.Description = rc.Description;
            row.Color = rc.Color;
            row.Components = rc.Components;
        }
        await db.SaveChangesAsync(ct);
        return source.Id;
    }

    public async Task<bool> EnqueueAsync(AppDbContext db, int sourceId, int userId, CancellationToken ct)
    {
        var source = await db.MigrationSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.UserId == userId, ct);
        if (source == null || source.Status is MigrationStatus.Queued or MigrationStatus.Running) return false;
        source.Status = MigrationStatus.Queued;
        source.Message = "Waiting to start…";
        await db.SaveChangesAsync(ct);
        _queue.Writer.TryWrite(sourceId);
        return true;
    }

    /// <summary>Runs an import. Existing items (matched by UID) are updated; nothing local is deleted.</summary>
    public async Task RunAsync(int sourceId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var calendars = scope.ServiceProvider.GetRequiredService<CalendarStore>();
        var books = scope.ServiceProvider.GetRequiredService<AddressBookStore>();
        var source = await db.MigrationSources.Include(s => s.Collections).Include(s => s.User).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return;

        source.Status = MigrationStatus.Running;
        source.LastRunUtc = DateTime.UtcNow;
        source.ItemsDone = source.ItemsFailed = source.ItemsTotal = 0;
        source.Message = "Connecting…";
        await SaveAsync(db, ct);

        try
        {
            if (source.ProtectedPassword == null) throw new RemoteDavException("No password stored; reconnect to the server.");
            string password;
            try { password = _protector.Unprotect(source.ProtectedPassword); }
            catch (System.Security.Cryptography.CryptographicException) { throw new RemoteDavException("The stored password can no longer be read; reconnect to the server."); }

            using var client = new RemoteDavClient(source.Username, password, source.AllowPrivateNetworks || AllowPrivate(source.User));
            var selected = source.Collections.Where(c => c.Selected).ToList();

            var listings = new List<(MigrationCollection Row, List<Uri> Hrefs)>();
            foreach (var row in selected)
            {
                try
                {
                    var hrefs = await client.ListAsync(new Uri(row.RemoteUrl), ct);
                    listings.Add((row, hrefs));
                    row.LastError = null;
                }
                catch (Exception ex) when (ex is RemoteDavException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    row.LastError = ex.Message;
                }
            }
            source.ItemsTotal = listings.Sum(l => l.Hrefs.Count);
            await SaveAsync(db, ct);

            foreach (var (row, hrefs) in listings)
            {
                source.Message = $"Importing \"{row.DisplayName}\"…";
                await SaveAsync(db, ct);
                row.ItemsImported = 0;
                try
                {
                    foreach (var batch in hrefs.Chunk(BatchSize))
                    {
                        var data = await client.FetchAsync(new Uri(row.RemoteUrl), row.Kind, batch, ct);
                        source.ItemsFailed += batch.Length - data.Count;
                        using (await gate.EnterAsync(ct))
                        {
                            foreach (var (href, content) in data)
                            {
                                var name = Uri.UnescapeDataString(href.AbsolutePath.TrimEnd('/').Split('/').Last());
                                var ok = row.Kind == "calendar"
                                    ? await ImportEventAsync(db, calendars, source, row, name, content, ct)
                                    : await ImportCardAsync(db, books, source, row, name, content, ct);
                                if (ok) { row.ItemsImported++; source.ItemsDone++; }
                                else source.ItemsFailed++;
                            }
                        }
                        await SaveAsync(db, ct);
                    }
                }
                catch (QuotaExceededException)
                {
                    row.LastError = "Storage quota exceeded";
                    throw new RemoteDavException("Your storage quota was reached; the import stopped.");
                }
                catch (Exception ex) when (ex is RemoteDavException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    row.LastError = ex.Message;
                }
            }

            var errors = source.Collections.Count(c => c.Selected && c.LastError != null);
            source.Status = errors > 0 && source.ItemsDone == 0 ? MigrationStatus.Failed : MigrationStatus.Completed;
            source.Message = $"Imported {source.ItemsDone} item(s)" +
                             (source.ItemsFailed > 0 ? $", {source.ItemsFailed} could not be imported" : "") +
                             (errors > 0 ? $"; {errors} collection(s) had errors" : "") + ".";
        }
        catch (Exception ex) when (ex is RemoteDavException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            source.Status = MigrationStatus.Failed;
            source.Message = ex.Message;
            log.LogInformation("Migration {Id} failed: {Message}", sourceId, ex.Message);
        }
        await SaveAsync(db, CancellationToken.None);
    }

    private async Task SaveAsync(AppDbContext db, CancellationToken ct)
    {
        using (await gate.EnterAsync(ct)) await db.SaveChangesAsync(ct);
    }

    private static string? SafeName(string name, string extension) =>
        Names.IsValidSegment(name) && name.Length <= 120 ? name : null;

    private static async Task<Calendar> TargetCalendarAsync(AppDbContext db, CalendarStore store, MigrationSource source, MigrationCollection row, CancellationToken ct)
    {
        if (row.LocalCalendarId is { } id && await db.Calendars.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == source.UserId, ct) is { } existing)
            return existing;
        var uri = await store.UniqueHomeUriAsync(source.UserId, Names.Slugify(row.DisplayName, "imported"), ct);
        var color = row.Color is { Length: >= 7 } c && Names.IsValidColor(c) ? c : null;
        var cal = await store.CreateAsync(source.UserId, uri, row.DisplayName,
            (row.Components ?? "VEVENT,VTODO,VJOURNAL").Split(','), row.Description, color, ct);
        row.LocalCalendarId = cal.Id;
        return cal;
    }

    private static async Task<AddressBook> TargetBookAsync(AppDbContext db, AddressBookStore store, MigrationSource source, MigrationCollection row, CancellationToken ct)
    {
        if (row.LocalAddressBookId is { } id && await db.AddressBooks.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == source.UserId, ct) is { } existing)
            return existing;
        var uri = await store.UniqueHomeUriAsync(source.UserId, Names.Slugify(row.DisplayName, "imported"), ct);
        var book = await store.CreateAsync(source.UserId, uri, row.DisplayName, row.Description, ct);
        row.LocalAddressBookId = book.Id;
        return book;
    }

    private async Task<bool> ImportEventAsync(AppDbContext db, CalendarStore store, MigrationSource source, MigrationCollection row,
        string name, string data, CancellationToken ct)
    {
        CalendarObjectInfo info;
        try { info = CalendarObjectInfo.Analyze(data); }
        catch (InvalidObjectException ex)
        {
            log.LogInformation("Skipping invalid calendar object {Name}: {Message}", name, ex.Message);
            return false;
        }
        var cal = await TargetCalendarAsync(db, store, source, row, ct);
        if (!cal.ComponentList.Contains(info.ComponentType))
        {
            if (!CalendarStore.SupportedComponents.Contains(info.ComponentType)) return false;
            cal.Components = string.Join(',', cal.ComponentList.Append(info.ComponentType));
            await db.SaveChangesAsync(ct);
        }
        var existing = await db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Id && o.Uid == info.Uid, ct);
        if (existing?.Data == data) return true;
        var uri = existing?.Uri ?? SafeName(name, ".ics") ?? Guid.NewGuid().ToString("N") + ".ics";
        if (existing == null && await db.CalendarObjects.AnyAsync(o => o.CalendarId == cal.Id && o.Uri == uri, ct))
            uri = Guid.NewGuid().ToString("N") + ".ics";
        try { await store.PutAsync(cal, uri, data, info, existing, ct: ct); }
        catch (UidConflictException) { return false; }
        return true;
    }

    private async Task<bool> ImportCardAsync(AppDbContext db, AddressBookStore store, MigrationSource source, MigrationCollection row,
        string name, string data, CancellationToken ct)
    {
        CardInfo info;
        try
        {
            if (data.Contains("VERSION:2.1", StringComparison.OrdinalIgnoreCase)) data = LegacyFormats.VCardTo30(data);
            info = CardInfo.Analyze(data, Path.GetFileNameWithoutExtension(name));
        }
        catch (Exception ex) when (ex is InvalidObjectException or ContentParseException)
        {
            log.LogInformation("Skipping invalid vCard {Name}: {Message}", name, ex.Message);
            return false;
        }
        var book = await TargetBookAsync(db, store, source, row, ct);
        var existing = await db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Id && c.Uid == info.Uid, ct);
        if (existing?.Data == data) return true;
        var uri = existing?.Uri ?? SafeName(name, ".vcf") ?? Guid.NewGuid().ToString("N") + ".vcf";
        if (existing == null && await db.Cards.AnyAsync(c => c.AddressBookId == book.Id && c.Uri == uri, ct))
            uri = Guid.NewGuid().ToString("N") + ".vcf";
        try { await store.PutAsync(book, uri, data, info, existing, ct); }
        catch (UidConflictException) { return false; }
        return true;
    }
}

/// <summary>Runs queued imports one at a time.</summary>
public sealed class MigrationWorker(MigrationService migrations, IServiceScopeFactory scopes, ILogger<MigrationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Jobs interrupted by a restart are marked as failed so they can be started again.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.MigrationSources.Where(s => s.Status == MigrationStatus.Running || s.Status == MigrationStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MigrationStatus.Failed).SetProperty(x => x.Message, "Interrupted by a server restart; start it again."), stoppingToken);
        }

        await foreach (var id in migrations.Queue.ReadAllAsync(stoppingToken))
        {
            try { await migrations.RunAsync(id, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Migration {Id} crashed", id);
            }
        }
    }
}
