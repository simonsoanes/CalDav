// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using Nullify.CalDav.Services;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

/// <summary>Computes live WebDAV, CalDAV, CardDAV, scheduling, quota and push properties for resources.</summary>
public sealed class DavPropertyProvider(DavResolver resolver, IServiceProvider services)
{
    public const string SyncTokenPrefix = "https://nullify.net/ns/caldav/sync/";

    private DavPaths Paths => resolver.Paths;
    private AppDbContext Db => resolver.Db;
    private ServerSettings Settings => services.GetRequiredService<SettingsService>().Current;

    public static string FormatSyncToken(long token) => SyncTokenPrefix + token.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseSyncToken(string? text, out long token)
    {
        token = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var slash = text.LastIndexOf('/');
        return long.TryParse(slash >= 0 ? text[(slash + 1)..] : text, NumberStyles.None, CultureInfo.InvariantCulture, out token);
    }

    private static readonly XName[] Common =
    [
        D + "resourcetype", D + "displayname", D + "current-user-principal", D + "principal-collection-set",
        D + "owner", D + "current-user-privilege-set", D + "supported-report-set",
    ];

    private static readonly XName[] QuotaProps = [D + "quota-used-bytes", D + "quota-available-bytes"];
    private static readonly XName[] PushProps = [P + "transports", P + "topic", P + "supported-triggers"];

    public IReadOnlyList<XName> AllPropNames(DavNode node) => node switch
    {
        PrincipalNode =>
        [
            .. Common, D + "principal-URL", C + "calendar-home-set", CR + "addressbook-home-set",
            C + "calendar-user-address-set", CS + "email-address-set", C + "calendar-user-type",
            D + "group-membership", D + "group-member-set", D + "alternate-URI-set",
            C + "schedule-inbox-URL", C + "schedule-outbox-URL",
        ],
        ProxyPrincipalNode or GroupPrincipalNode => [.. Common, D + "principal-URL", D + "group-member-set", D + "group-membership"],
        CalendarHomeNode or AddressBookHomeNode => [.. Common, .. QuotaProps],
        CalendarNode { IsInbox: true } => [.. Common, C + "schedule-default-calendar-URL", C + "supported-calendar-component-set"],
        CalendarNode =>
        [
            .. Common, C + "calendar-description", C + "supported-calendar-component-set", C + "supported-calendar-data",
            C + "max-resource-size", C + "calendar-timezone", A + "calendar-color", A + "calendar-order",
            CS + "getctag", D + "sync-token", C + "supported-collation-set", C + "schedule-calendar-transp",
            .. QuotaProps, .. PushProps,
        ],
        AddressBookNode =>
        [
            .. Common, CR + "addressbook-description", CR + "supported-address-data", CR + "max-resource-size",
            CS + "getctag", D + "sync-token", CR + "supported-collation-set", .. QuotaProps, .. PushProps,
        ],
        CalendarObjectNode =>
        [
            D + "resourcetype", D + "getetag", D + "getcontenttype", D + "getcontentlength", D + "getlastmodified",
            D + "owner", D + "current-user-privilege-set", C + "schedule-tag",
        ],
        CardNode =>
        [
            D + "resourcetype", D + "getetag", D + "getcontenttype", D + "getcontentlength", D + "getlastmodified",
            D + "owner", D + "current-user-privilege-set",
        ],
        _ => Common,
    };

    public async Task<List<(XElement Prop, int Status)>> GetManyAsync(DavNode node, IEnumerable<XElement> requested, CancellationToken ct)
    {
        var result = new List<(XElement, int)>();
        foreach (var req in requested)
        {
            var value = await GetAsync(node, req.Name, req, ct);
            result.Add(value == null ? (new XElement(req.Name), 404) : (value, 200));
        }
        return result;
    }

    public async Task<List<(XElement Prop, int Status)>> GetAllAsync(DavNode node, IEnumerable<XElement>? include, CancellationToken ct)
    {
        var names = AllPropNames(node).ToList();
        if (include != null) names.AddRange(include.Select(i => i.Name).Where(n => !names.Contains(n)));
        var result = new List<(XElement, int)>();
        foreach (var name in names)
        {
            var value = await GetAsync(node, name, null, ct);
            if (value != null) result.Add((value, 200));
        }
        return result;
    }

    private static XElement Prop(XName name, params object?[] content) => new(name, content);

    private XElement HrefProp(XName name, params string[] hrefs) => Prop(name, hrefs.Select(DavXml.Href));

    private static XElement Privileges(params XName[] privileges) =>
        Prop(D + "current-user-privilege-set", privileges.Select(p => new XElement(D + "privilege", new XElement(p))));

    private static readonly XName[] FullPrivileges =
    [
        D + "all", D + "read", D + "write", D + "write-properties", D + "write-content", D + "bind", D + "unbind",
        D + "read-acl", D + "read-current-user-privilege-set", C + "read-free-busy",
    ];

    private static readonly XName[] ReadWritePrivileges =
    [
        D + "read", D + "write", D + "write-properties", D + "write-content", D + "bind", D + "unbind",
        D + "read-current-user-privilege-set", C + "read-free-busy",
    ];

    private static readonly XName[] ReadPrivileges =
    [
        D + "read", D + "read-current-user-privilege-set", C + "read-free-busy",
    ];

    /// <summary>Owner of a read-only (subscribed/generated) collection may rename it but not change its contents.</summary>
    private static readonly XName[] OwnerReadOnlyPrivileges =
    [
        D + "read", D + "write-properties", D + "read-current-user-privilege-set", C + "read-free-busy",
    ];

    private static readonly XName[] InboxPrivileges =
    [
        D + "read", D + "unbind", D + "write-properties", D + "read-current-user-privilege-set", C + "schedule-deliver",
    ];

    private static XElement Reports(params XName[] reports) =>
        Prop(D + "supported-report-set",
            reports.Select(r => new XElement(D + "supported-report", new XElement(D + "report", new XElement(r)))));

    private static readonly XName[] PrincipalReports =
    [
        D + "principal-property-search", D + "principal-search-property-set", D + "expand-property", D + "principal-match",
    ];

    public async Task<XElement?> GetAsync(DavNode node, XName name, XElement? request, CancellationToken ct)
    {
        // Properties available on every resource.
        if (name == D + "current-user-principal") return HrefProp(name, Paths.Principal(resolver.CurrentUser.Username));
        if (name == D + "principal-collection-set") return HrefProp(name, Paths.Principals, Paths.Groups);

        switch (node)
        {
            case RootNode:
                if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"));
                if (name == D + "displayname") return Prop(name, "Nullify.CalDav");
                if (name == D + "supported-report-set") return Reports(PrincipalReports);
                if (name == D + "current-user-privilege-set") return Privileges(ReadPrivileges);
                return null;

            case PrincipalsNode or GroupsNode:
                if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"));
                if (name == D + "displayname") return Prop(name, node is GroupsNode ? "Groups" : "Principals");
                if (name == D + "supported-report-set") return Reports(PrincipalReports);
                if (name == D + "current-user-privilege-set") return Privileges(ReadPrivileges);
                return null;

            case PrincipalNode p:
                return await PrincipalPropertyAsync(p, name, ct);

            case ProxyPrincipalNode px:
                return await ProxyPropertyAsync(px, name, ct);

            case GroupPrincipalNode g:
                return await GroupPropertyAsync(g, name, ct);

            case CalendarHomeNode h:
                if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"));
                if (name == D + "displayname") return Prop(name, "Calendars");
                if (name == D + "owner") return HrefProp(name, Paths.Principal(h.Owner.Username));
                if (name == D + "current-user-privilege-set")
                    return Privileges(h.Access == NodeAccess.Owner ? FullPrivileges : h.Access == NodeAccess.ReadWrite ? ReadWritePrivileges : ReadPrivileges);
                if (name == D + "supported-report-set") return Reports(PrincipalReports);
                if (QuotaProps.Contains(name)) return await QuotaAsync(h.Owner.Id, name, ct);
                return HomeSetProperty(h.Owner, name);

            case AddressBookHomeNode h:
                if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"));
                if (name == D + "displayname") return Prop(name, "Address Books");
                if (name == D + "owner") return HrefProp(name, Paths.Principal(h.Owner.Username));
                if (name == D + "current-user-privilege-set") return Privileges(FullPrivileges);
                if (name == D + "supported-report-set") return Reports(PrincipalReports);
                if (QuotaProps.Contains(name)) return await QuotaAsync(h.Owner.Id, name, ct);
                return HomeSetProperty(h.Owner, name);

            case OutboxNode o:
                if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"), new XElement(C + "schedule-outbox"));
                if (name == D + "displayname") return Prop(name, "Outbox");
                if (name == D + "owner") return HrefProp(name, Paths.Principal(o.Owner.Username));
                if (name == D + "current-user-privilege-set")
                    return Privileges(D + "read", D + "read-current-user-privilege-set", C + "schedule-send", C + "schedule-query-freebusy");
                return null;

            case CalendarNode c:
                if (QuotaProps.Contains(name)) return await QuotaAsync(c.Calendar.OwnerId, name, ct);
                if (PushProps.Contains(name)) return PushProperty(CollectionChange.Calendar, c.Calendar.Id, name);
                if (c.IsInbox) return InboxProperty(c, name);
                if (SharingProps.Contains(name)) return await SharingPropertyAsync(c, name, ct);
                return CalendarProperty(c, name);

            case AddressBookNode b:
                if (QuotaProps.Contains(name)) return await QuotaAsync(b.Book.OwnerId, name, ct);
                if (PushProps.Contains(name)) return PushProperty(CollectionChange.AddressBook, b.Book.Id, name);
                return AddressBookProperty(b, name);

            case CalendarObjectNode o:
                if (name == D + "resourcetype") return Prop(name);
                if (name == D + "getetag") return Prop(name, o.Object.ETag);
                if (name == D + "getcontenttype")
                    return Prop(name, $"text/calendar; charset=utf-8; component={o.Object.ComponentType.ToLowerInvariant()}");
                if (name == D + "getcontentlength") return Prop(name, o.Object.Size);
                if (name == D + "getlastmodified") return Prop(name, o.Object.ModifiedUtc.ToString("R", CultureInfo.InvariantCulture));
                if (name == D + "creationdate") return Prop(name, DateTime.SpecifyKind(o.Object.CreatedUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                if (name == D + "owner") return HrefProp(name, Paths.Principal(o.Calendar.Calendar.Owner.Username));
                if (name == D + "current-user-privilege-set")
                    return Privileges(o.Calendar.CanWrite ? (o.Calendar.IsOwner ? FullPrivileges : ReadWritePrivileges)
                        : o.Calendar.CanDelete ? [D + "read", D + "unbind", D + "read-current-user-privilege-set"] : ReadPrivileges);
                if (name == C + "schedule-tag") return o.Object.ScheduleTag == null ? null : Prop(name, o.Object.ScheduleTag);
                if (name == C + "calendar-data") return Prop(name, await resolver.GetDataAsync(o, ct));
                return null;

            case CardNode k:
                if (name == D + "resourcetype") return Prop(name);
                if (name == D + "getetag") return Prop(name, k.Card.ETag);
                if (name == D + "getcontenttype") return Prop(name, "text/vcard; charset=utf-8");
                if (name == D + "getcontentlength") return Prop(name, k.Card.Size);
                if (name == D + "getlastmodified") return Prop(name, k.Card.ModifiedUtc.ToString("R", CultureInfo.InvariantCulture));
                if (name == D + "owner") return HrefProp(name, Paths.Principal(k.Book.Book.Owner.Username));
                if (name == D + "current-user-privilege-set")
                    return Privileges(k.Book.CanWrite ? (k.Book.IsOwner ? FullPrivileges : ReadWritePrivileges) : ReadPrivileges);
                if (name == CR + "address-data")
                {
                    var data = await resolver.GetDataAsync(k, ct);
                    var version = (string?)request?.Attribute("version");
                    return Prop(name, VCardVersions.Convert(data, version == "4.0" ? "4.0" : "3.0"));
                }
                return null;
        }
        return null;
    }

    // ------------------------------------------------------------------ Principals

    private XElement? HomeSetProperty(User owner, XName name)
    {
        if (name == C + "calendar-home-set") return HrefProp(name, Paths.CalendarHome(owner.Username));
        if (name == CR + "addressbook-home-set") return HrefProp(name, Paths.AddressBookHome(owner.Username));
        return null;
    }

    private async Task<XElement?> PrincipalPropertyAsync(PrincipalNode p, XName name, CancellationToken ct)
    {
        var u = p.User;
        var href = Paths.Principal(u.Username);
        var isSelf = u.Id == resolver.CurrentUser.Id;
        if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"), new XElement(D + "principal"));
        if (name == D + "displayname") return Prop(name, string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName);
        if (name == D + "principal-URL") return HrefProp(name, href);
        if (name == D + "owner") return HrefProp(name, href);
        if (name == D + "current-user-privilege-set") return Privileges(ReadPrivileges);
        if (name == D + "supported-report-set") return Reports(PrincipalReports);
        if (name == D + "alternate-URI-set")
            return Prop(name, string.IsNullOrEmpty(u.Email) ? null : DavXml.Href("mailto:" + u.Email));
        if (name == D + "group-member-set") return Prop(name);
        if (name == D + "group-membership")
        {
            var groups = await Db.GroupMembers.Where(m => m.UserId == u.Id).Select(m => m.Group.Name).ToListAsync(ct);
            var delegations = await Db.Delegations.Where(d => d.DelegateId == u.Id && d.Owner.IsEnabled)
                .Select(d => new { d.Owner.Username, d.CanWrite }).ToListAsync(ct);
            return HrefProp(name, [.. groups.Select(Paths.Group), .. delegations.Select(d => Paths.ProxyPrincipal(d.Username, d.CanWrite))]);
        }
        if (name == CS + "calendar-proxy-read-for" || name == CS + "calendar-proxy-write-for")
        {
            var write = name == CS + "calendar-proxy-write-for";
            var owners = await Db.Delegations.Where(d => d.DelegateId == u.Id && d.CanWrite == write && d.Owner.IsEnabled)
                .Select(d => d.Owner.Username).ToListAsync(ct);
            return HrefProp(name, owners.Select(Paths.Principal).ToArray());
        }
        if (name == C + "calendar-user-type") return Prop(name, "INDIVIDUAL");
        if (name == C + "calendar-user-address-set")
            return Prop(name, string.IsNullOrEmpty(u.Email) ? null : DavXml.Href("mailto:" + u.Email), DavXml.Href(href));
        if (name == CS + "email-address-set")
            return Prop(name, string.IsNullOrEmpty(u.Email) ? null : new XElement(CS + "email-address", u.Email));
        if (isSelf)
        {
            if (name == C + "schedule-inbox-URL" && Settings.SchedulingEnabled) return HrefProp(name, Paths.Inbox(u.Username));
            if (name == C + "schedule-outbox-URL" && Settings.SchedulingEnabled) return HrefProp(name, Paths.Outbox(u.Username));
            return HomeSetProperty(u, name);
        }
        return null;
    }

    private async Task<XElement?> ProxyPropertyAsync(ProxyPrincipalNode px, XName name, CancellationToken ct)
    {
        if (name == D + "resourcetype")
            return Prop(name, new XElement(D + "principal"), new XElement(CS + (px.Write ? "calendar-proxy-write" : "calendar-proxy-read")));
        if (name == D + "displayname")
            return Prop(name, $"{px.Owner.DisplayName} ({(px.Write ? "calendar delegates, read/write" : "calendar delegates, read-only")})");
        if (name == D + "principal-URL" || name == D + "owner") return HrefProp(name, px.Href);
        if (name == D + "current-user-privilege-set") return Privileges(ReadPrivileges);
        if (name == D + "group-membership") return Prop(name);
        if (name == D + "group-member-set")
        {
            var delegates = await Db.Delegations.Where(d => d.OwnerId == px.Owner.Id && d.CanWrite == px.Write && d.Delegate.IsEnabled)
                .Select(d => d.Delegate.Username).ToListAsync(ct);
            return HrefProp(name, delegates.Select(Paths.Principal).ToArray());
        }
        // Lets clients that follow group memberships (e.g. DAVx⁵) discover the delegated calendars.
        if (name == C + "calendar-home-set")
        {
            var isDelegate = await Db.Delegations.AnyAsync(d => d.OwnerId == px.Owner.Id && d.DelegateId == resolver.CurrentUser.Id, ct);
            return isDelegate || px.Owner.Id == resolver.CurrentUser.Id ? HrefProp(name, Paths.CalendarHome(px.Owner.Username)) : null;
        }
        return null;
    }

    private async Task<XElement?> GroupPropertyAsync(GroupPrincipalNode g, XName name, CancellationToken ct)
    {
        if (name == D + "resourcetype") return Prop(name, new XElement(D + "principal"));
        if (name == D + "displayname") return Prop(name, g.Group.DisplayName);
        if (name == D + "principal-URL" || name == D + "owner") return HrefProp(name, g.Href);
        if (name == D + "current-user-privilege-set") return Privileges(ReadPrivileges);
        if (name == C + "calendar-user-type") return Prop(name, "GROUP");
        if (name == D + "group-membership") return Prop(name);
        if (name == D + "group-member-set")
        {
            var members = await Db.GroupMembers.Where(m => m.GroupId == g.Group.Id && m.User.IsEnabled).Select(m => m.User.Username).ToListAsync(ct);
            return HrefProp(name, members.Select(Paths.Principal).ToArray());
        }
        return null;
    }

    // ------------------------------------------------------------------ Quota (RFC 4331) and push (WebDAV-Push)

    private readonly Dictionary<int, (long Used, long? Limit)> _quotaCache = new();

    private async Task<XElement?> QuotaAsync(int ownerId, XName name, CancellationToken ct)
    {
        if (!_quotaCache.TryGetValue(ownerId, out var q))
        {
            var quota = services.GetRequiredService<QuotaService>();
            q = (await quota.UsedAsync(Db, ownerId, ct), await quota.LimitAsync(Db, ownerId, ct));
            _quotaCache[ownerId] = q;
        }
        if (name == D + "quota-used-bytes") return Prop(name, q.Used);
        return q.Limit is { } limit ? Prop(name, Math.Max(0, limit - q.Used)) : null;
    }

    private XElement? PushProperty(string type, int collectionId, XName name)
    {
        var push = services.GetRequiredService<PushService>();
        if (!Settings.PushEnabled) return null;
        if (name == P + "transports")
            return Prop(name, new XElement(P + "web-push",
                new XElement(P + "vapid-public-key", new XAttribute("type", "p256ecdsa"), push.VapidPublicKey)));
        if (name == P + "topic") return Prop(name, push.Topic(type, collectionId));
        if (name == P + "supported-triggers")
            return Prop(name,
                new XElement(P + "content-update", new XElement(D + "depth", "1")),
                new XElement(P + "property-update", new XElement(D + "depth", "0")));
        return null;
    }

    // ------------------------------------------------------------------ Collections

    private XElement? InboxProperty(CalendarNode c, XName name)
    {
        if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"), new XElement(C + "schedule-inbox"));
        if (name == D + "displayname") return Prop(name, "Inbox");
        if (name == D + "owner") return HrefProp(name, Paths.Principal(c.Calendar.Owner.Username));
        if (name == D + "current-user-privilege-set") return Privileges(InboxPrivileges);
        if (name == D + "supported-report-set") return Reports(C + "calendar-query", C + "calendar-multiget", D + "sync-collection");
        if (name == D + "sync-token") return Prop(name, FormatSyncToken(c.Calendar.SyncToken));
        if (name == CS + "getctag") return Prop(name, FormatSyncToken(c.Calendar.SyncToken));
        if (name == C + "supported-calendar-component-set")
            return Prop(name, new[] { "VEVENT", "VTODO" }.Select(n => new XElement(C + "comp", new XAttribute("name", n))));
        if (name == C + "schedule-default-calendar-URL")
        {
            var def = Db.Calendars.Where(x => x.OwnerId == c.Calendar.OwnerId && x.Kind == CalendarKind.Normal && x.Components.Contains("VEVENT"))
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.Uri).FirstOrDefault();
            return def == null ? Prop(name) : HrefProp(name, Paths.Calendar(c.Calendar.Owner.Username, def));
        }
        return null;
    }

    private XElement? CalendarProperty(CalendarNode c, XName name)
    {
        var cal = c.Calendar;
        if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"), new XElement(C + "calendar"));
        if (name == D + "displayname") return Prop(name, c.DisplayName);
        if (name == D + "owner") return HrefProp(name, Paths.Principal(cal.Owner.Username));
        if (name == D + "current-user-privilege-set")
            return Privileges(c.CanWrite
                ? (c.IsOwner ? FullPrivileges : ReadWritePrivileges)
                : c.IsOwner ? OwnerReadOnlyPrivileges : ReadPrivileges);
        if (name == D + "supported-report-set")
            return Reports(C + "calendar-query", C + "calendar-multiget", C + "free-busy-query", D + "sync-collection", D + "expand-property");
        if (name == D + "sync-token") return Prop(name, FormatSyncToken(cal.SyncToken));
        if (name == CS + "getctag") return Prop(name, FormatSyncToken(cal.SyncToken));
        if (name == D + "getlastmodified") return Prop(name, cal.ModifiedUtc.ToString("R", CultureInfo.InvariantCulture));
        if (name == C + "calendar-description") return Prop(name, cal.Description ?? "");
        if (name == C + "supported-calendar-component-set")
            return Prop(name, cal.ComponentList.Select(n => new XElement(C + "comp", new XAttribute("name", n))));
        if (name == C + "supported-calendar-data")
            return Prop(name, new XElement(C + "calendar-data", new XAttribute("content-type", "text/calendar"), new XAttribute("version", "2.0")));
        if (name == C + "max-resource-size") return Prop(name, CalendarStore.MaxResourceSize);
        if (name == C + "calendar-timezone") return cal.TimeZone == null ? null : Prop(name, cal.TimeZone);
        if (name == C + "supported-collation-set")
            return Prop(name, new[] { "i;ascii-casemap", "i;octet", "i;unicode-casemap" }.Select(x => new XElement(C + "supported-collation", x)));
        if (name == C + "schedule-calendar-transp")
            return Prop(name, new XElement(C + (cal.Kind == CalendarKind.Normal ? "opaque" : "transparent")));
        if (name == A + "calendar-color") return c.Color == null ? null : Prop(name, c.Color);
        if (name == A + "calendar-order") return Prop(name, cal.SortOrder);
        if (name == CS + "source" && cal.SubscriptionUrl != null) return HrefProp(name, cal.SubscriptionUrl);
        if (name == CS + "shared-url" && !c.IsOwner) return HrefProp(name, Paths.Calendar(cal.Owner.Username, cal.Uri));
        return null;
    }

    private static readonly XName[] SharingProps = [D + "invite", CS + "invite", D + "share-access", CS + "allowed-sharing-modes"];

    /// <summary>
    /// Sharing as clients see it (draft-pot-webdav-resource-sharing and CalendarServer): who the
    /// owner shares a calendar with, and the current user's access to it. Shares need no
    /// accepting, so every sharee is reported as having accepted.
    /// </summary>
    private async Task<XElement?> SharingPropertyAsync(CalendarNode c, XName name, CancellationToken ct)
    {
        var cal = c.Calendar;
        if (name == CS + "allowed-sharing-modes") return c.IsOwner ? Prop(name, new XElement(CS + "can-be-shared")) : null;
        if (!c.IsOwner)
        {
            if (name != D + "share-access") return null;
            return c.Share == null ? null : Prop(name, new XElement(D + (c.Share.Access == ShareAccess.ReadWrite ? "read-write" : "read")));
        }

        var shares = await Db.CalendarShares.Where(s => s.CalendarId == cal.Id && (s.Sharee != null || s.Group != null))
            .Include(s => s.Sharee).Include(s => s.Group).OrderBy(s => s.Id).ToListAsync(ct);
        if (name == D + "share-access") return Prop(name, new XElement(D + (shares.Count > 0 ? "shared-owner" : "not-shared")));

        var sharees = shares.Select(s => (
            Href: s.Sharee != null
                ? (string.IsNullOrEmpty(s.Sharee.Email) ? Paths.Principal(s.Sharee.Username) : "mailto:" + s.Sharee.Email)
                : Paths.Group(s.Group!.Name),
            Name: s.Sharee?.DisplayName ?? s.Group!.DisplayName,
            ReadWrite: s.Access == ShareAccess.ReadWrite)).ToList();
        var owner = cal.Owner;
        if (name == D + "invite")
            return Prop(name,
                new XElement(D + "sharee", DavXml.Href(Paths.Principal(owner.Username)),
                    new XElement(D + "prop", new XElement(D + "displayname", owner.DisplayName)),
                    new XElement(D + "share-access", new XElement(D + "shared-owner")), new XElement(D + "invite-accepted")),
                sharees.Select(s => new XElement(D + "sharee", DavXml.Href(s.Href),
                    new XElement(D + "prop", new XElement(D + "displayname", s.Name)),
                    new XElement(D + "share-access", new XElement(D + (s.ReadWrite ? "read-write" : "read"))),
                    new XElement(D + "invite-accepted"))));
        return Prop(name,
            new XElement(CS + "organizer", DavXml.Href(Paths.Principal(owner.Username)), new XElement(CS + "common-name", owner.DisplayName)),
            sharees.Select(s => new XElement(CS + "user", DavXml.Href(s.Href), new XElement(CS + "common-name", s.Name),
                new XElement(CS + "invite-accepted"), new XElement(CS + "access", new XElement(CS + (s.ReadWrite ? "read-write" : "read"))))));
    }

    private XElement? AddressBookProperty(AddressBookNode b, XName name)
    {
        var book = b.Book;
        if (name == D + "resourcetype") return Prop(name, new XElement(D + "collection"), new XElement(CR + "addressbook"));
        if (name == D + "displayname") return Prop(name, b.DisplayName);
        if (name == D + "owner") return HrefProp(name, Paths.Principal(book.Owner.Username));
        if (name == D + "current-user-privilege-set")
            return Privileges(b.CanWrite ? (b.IsOwner ? FullPrivileges : ReadWritePrivileges) : b.IsOwner ? OwnerReadOnlyPrivileges : ReadPrivileges);
        if (name == D + "supported-report-set")
            return Reports(CR + "addressbook-query", CR + "addressbook-multiget", D + "sync-collection", D + "expand-property");
        if (name == D + "sync-token") return Prop(name, FormatSyncToken(book.SyncToken));
        if (name == CS + "getctag") return Prop(name, FormatSyncToken(book.SyncToken));
        if (name == D + "getlastmodified") return Prop(name, book.ModifiedUtc.ToString("R", CultureInfo.InvariantCulture));
        if (name == CR + "addressbook-description") return Prop(name, book.Description ?? "");
        if (name == CR + "supported-address-data")
            return Prop(name,
                new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "3.0")),
                new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "4.0")));
        if (name == CR + "max-resource-size") return Prop(name, AddressBookStore.MaxResourceSize);
        if (name == CR + "supported-collation-set")
            return Prop(name, new[] { "i;ascii-casemap", "i;octet", "i;unicode-casemap" }.Select(x => new XElement(CR + "supported-collation", x)));
        return null;
    }
}
