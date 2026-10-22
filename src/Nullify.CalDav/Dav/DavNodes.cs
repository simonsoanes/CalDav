// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Dav;

/// <summary>Builds and parses hrefs relative to the DAV mount point.</summary>
public sealed class DavPaths(string basePath)
{
    public string Base { get; } = basePath.TrimEnd('/');

    public string Root => Base + "/";
    public string Principals => Base + "/principals/";
    public string Principal(string user) => Principals + Escape(user) + "/";
    public string ProxyPrincipal(string user, bool write) => Principal(user) + (write ? "calendar-proxy-write/" : "calendar-proxy-read/");
    public string Groups => Base + "/groups/";
    public string Group(string name) => Groups + Escape(name) + "/";
    public string CalendarHome(string user) => Base + "/calendars/" + Escape(user) + "/";
    public string Calendar(string user, string uri) => CalendarHome(user) + Escape(uri) + "/";
    public string CalendarObject(string user, string cal, string obj) => Calendar(user, cal) + Escape(obj);
    public string Inbox(string user) => CalendarHome(user) + "inbox/";
    public string Outbox(string user) => CalendarHome(user) + "outbox/";
    public string AddressBookHome(string user) => Base + "/addressbooks/" + Escape(user) + "/";
    public string AddressBook(string user, string uri) => AddressBookHome(user) + Escape(uri) + "/";
    public string Card(string user, string book, string card) => AddressBook(user, book) + Escape(card);
    public string PushRegistration(string id) => Base + "/push/" + Escape(id);

    /// <summary>Percent-encodes a path segment, keeping RFC 3986 pchar characters readable.</summary>
    public static string Escape(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@".Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Converts an href (absolute URL or path) into decoded segments below the DAV root, or null if outside.</summary>
    public string[]? ParseHref(string href)
    {
        href = href.Trim();
        string path;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
            path = abs.AbsolutePath;
        else
        {
            path = href;
            var q = path.IndexOfAny(['?', '#']);
            if (q >= 0) path = path[..q];
        }

        var decodedBase = Base;
        if (!string.IsNullOrEmpty(decodedBase))
        {
            if (path.StartsWith(decodedBase + "/", StringComparison.Ordinal)) path = path[decodedBase.Length..];
            else if (path == decodedBase) path = "/";
            else
            {
                var escapedBase = string.Join('/', decodedBase.Split('/').Select(Escape));
                if (path.StartsWith(escapedBase + "/", StringComparison.OrdinalIgnoreCase)) path = path[escapedBase.Length..];
                else return null;
            }
        }
        return SplitPath(path, decode: true);
    }

    public static string[] SplitPath(string path, bool decode) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => decode ? Uri.UnescapeDataString(s) : s)
            .ToArray();
}

/// <summary>Access the current user has to a resource.</summary>
public enum NodeAccess
{
    Owner,
    ReadWrite,
    Read,
}

public abstract class DavNode
{
    public abstract string Href { get; }
    public virtual bool IsCollection => true;
}

public sealed class RootNode(string href) : DavNode
{
    public override string Href => href;
}

public sealed class PrincipalsNode(string href) : DavNode
{
    public override string Href => href;
}

public sealed class PrincipalNode(User user, string href) : DavNode
{
    public User User { get; } = user;
    public override string Href => href;
}

/// <summary>CalendarServer calendar-proxy-read / calendar-proxy-write group principal of a user.</summary>
public sealed class ProxyPrincipalNode(User owner, bool write, string href) : DavNode
{
    public User Owner { get; } = owner;
    public bool Write { get; } = write;
    public override string Href => href;
}

public sealed class GroupsNode(string href) : DavNode
{
    public override string Href => href;
}

public sealed class GroupPrincipalNode(Group group, string href) : DavNode
{
    public Group Group { get; } = group;
    public override string Href => href;
}

public sealed class CalendarHomeNode(User owner, string href, NodeAccess access = NodeAccess.Owner) : DavNode
{
    public User Owner { get; } = owner;
    public NodeAccess Access { get; } = access;
    public override string Href => href;
}

public sealed class OutboxNode(User owner, string href) : DavNode
{
    public User Owner { get; } = owner;
    public override string Href => href;
}

public sealed class AddressBookHomeNode(User owner, string href) : DavNode
{
    public User Owner { get; } = owner;
    public override string Href => href;
}

/// <summary>A calendar as seen from a particular user's home (owned, shared with them or delegated to them).</summary>
public sealed class CalendarNode(Calendar calendar, CalendarShare? share, User homeUser, string href, NodeAccess access) : DavNode
{
    public Calendar Calendar { get; } = calendar;
    public CalendarShare? Share { get; } = share;
    public User HomeUser { get; } = homeUser;
    public NodeAccess Access { get; } = access;
    public override string Href => href;

    /// <summary>The current user owns this calendar.</summary>
    public bool IsOwner => Access == NodeAccess.Owner && Share == null;

    public bool IsInbox => Calendar.Kind == CalendarKind.Inbox;

    /// <summary>Resources may be created or modified.</summary>
    public bool CanWrite => Access != NodeAccess.Read && !Calendar.IsReadOnlyKind && !IsInbox;

    /// <summary>Resources may be deleted (the inbox allows deleting processed messages).</summary>
    public bool CanDelete => CanWrite || (IsInbox && IsOwner);

    public string DisplayName => Share?.DisplayName ?? Calendar.DisplayName;
    public string? Color => Share?.Color ?? Calendar.Color;
}

public sealed class CalendarObjectNode(CalendarNode calendar, CalendarObject obj, string href) : DavNode
{
    public CalendarNode Calendar { get; } = calendar;
    public CalendarObject Object { get; } = obj;
    public override string Href => href;
    public override bool IsCollection => false;
}

public sealed class AddressBookNode(AddressBook book, AddressBookShare? share, User homeUser, string href, NodeAccess access) : DavNode
{
    public AddressBook Book { get; } = book;
    public AddressBookShare? Share { get; } = share;
    public User HomeUser { get; } = homeUser;
    public NodeAccess Access { get; } = access;
    public override string Href => href;
    public bool IsOwner => Access == NodeAccess.Owner && Share == null;
    public bool CanWrite => Access != NodeAccess.Read && Book.Kind == AddressBookKind.Normal;
    public string DisplayName => Share?.DisplayName ?? Book.DisplayName;
}

public sealed class CardNode(AddressBookNode book, Card card, string href) : DavNode
{
    public AddressBookNode Book { get; } = book;
    public Card Card { get; } = card;
    public override string Href => href;
    public override bool IsCollection => false;
}

public sealed record ResolveResult(DavNode? Node, DavNode? Parent, string? Name, bool Forbidden = false);

/// <summary>Maps request paths onto the DAV resource tree.</summary>
public sealed class DavResolver(AppDbContext db, User currentUser, DavPaths paths)
{
    private List<int>? _groupIds;

    public DavPaths Paths => paths;
    public User CurrentUser => currentUser;
    public AppDbContext Db => db;

    public async Task<List<int>> GroupIdsAsync(CancellationToken ct = default) =>
        _groupIds ??= await db.GroupMembers.Where(m => m.UserId == currentUser.Id).Select(m => m.GroupId).ToListAsync(ct);

    private static NodeAccess ShareAccessOf(ShareAccess a) => a == ShareAccess.ReadWrite ? NodeAccess.ReadWrite : NodeAccess.Read;

    /// <summary>Access the current user has to another user's calendar home (via delegation), or null.</summary>
    private async Task<NodeAccess?> HomeAccessAsync(User owner, CancellationToken ct)
    {
        if (owner.Id == currentUser.Id) return NodeAccess.Owner;
        var d = await db.Delegations.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerId == owner.Id && x.DelegateId == currentUser.Id, ct);
        return d == null ? null : d.CanWrite ? NodeAccess.ReadWrite : NodeAccess.Read;
    }

    private async Task<User?> UserAsync(string username, CancellationToken ct) =>
        string.Equals(username, currentUser.Username, StringComparison.OrdinalIgnoreCase)
            ? currentUser
            : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username && u.IsEnabled, ct);

    public async Task<ResolveResult> ResolveAsync(IReadOnlyList<string> s, CancellationToken ct)
    {
        if (s.Count == 0) return new(new RootNode(paths.Root), null, null);

        switch (s[0])
        {
            case "principals":
            {
                var principals = new PrincipalsNode(paths.Principals);
                if (s.Count == 1) return new(principals, new RootNode(paths.Root), "principals");
                var u = await UserAsync(s[1], ct);
                if (u == null || s.Count > 3) return new(null, null, null);
                var principal = new PrincipalNode(u, paths.Principal(u.Username));
                if (s.Count == 2) return new(principal, principals, s[1]);
                return s[2] switch
                {
                    "calendar-proxy-read" => new(new ProxyPrincipalNode(u, false, paths.ProxyPrincipal(u.Username, false)), principal, s[2]),
                    "calendar-proxy-write" => new(new ProxyPrincipalNode(u, true, paths.ProxyPrincipal(u.Username, true)), principal, s[2]),
                    _ => new(null, null, null),
                };
            }
            case "groups":
            {
                var groups = new GroupsNode(paths.Groups);
                if (s.Count == 1) return new(groups, new RootNode(paths.Root), "groups");
                if (s.Count > 2) return new(null, null, null);
                var g = await db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Name == s[1], ct);
                return new(g == null ? null : new GroupPrincipalNode(g, paths.Group(g.Name)), groups, s[1]);
            }
            case "calendars":
            {
                if (s.Count == 1) return new(null, new RootNode(paths.Root), "calendars");
                var owner = await UserAsync(s[1], ct);
                if (owner == null) return new(null, null, null, Forbidden: true);
                var access = await HomeAccessAsync(owner, ct);
                if (access == null) return new(null, null, null, Forbidden: true);
                var home = new CalendarHomeNode(owner, paths.CalendarHome(owner.Username), access.Value);
                if (s.Count == 2) return new(home, new RootNode(paths.Root), s[1]);

                if (s[2] == "outbox")
                {
                    if (access != NodeAccess.Owner) return new(null, null, null, Forbidden: true);
                    return s.Count == 3 ? new(new OutboxNode(owner, paths.Outbox(owner.Username)), home, s[2]) : new(null, null, null);
                }

                var cal = await LoadCalendarAsync(owner, access.Value, s[2], ct);
                if (s.Count == 3) return new(cal, home, s[2]);
                if (cal == null || s.Count > 4) return new(null, null, null);
                var obj = await db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Calendar.Id && o.Uri == s[3], ct);
                return new(obj == null ? null : ObjectNode(cal, obj), cal, s[3]);
            }
            case "addressbooks":
            {
                if (s.Count == 1) return new(null, new RootNode(paths.Root), "addressbooks");
                if (!string.Equals(s[1], currentUser.Username, StringComparison.OrdinalIgnoreCase)) return new(null, null, null, Forbidden: true);
                var home = new AddressBookHomeNode(currentUser, paths.AddressBookHome(currentUser.Username));
                if (s.Count == 2) return new(home, new RootNode(paths.Root), s[1]);
                var book = await LoadAddressBookAsync(s[2], ct);
                if (s.Count == 3) return new(book, home, s[2]);
                if (book == null || s.Count > 4) return new(null, null, null);
                var card = await db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Book.Id && c.Uri == s[3], ct);
                return new(card == null ? null : CardNode(book, card), book, s[3]);
            }
            default:
                return new(null, null, null);
        }
    }

    public CalendarObjectNode ObjectNode(CalendarNode cal, CalendarObject obj) =>
        new(cal, obj, cal.Href + DavPaths.Escape(obj.Uri));

    public CardNode CardNode(AddressBookNode book, Card card) =>
        new(book, card, book.Href + DavPaths.Escape(card.Uri));

    /// <summary>Loads a calendar of the current user's home (own or shared) by its path segment.</summary>
    public Task<CalendarNode?> LoadCalendarAsync(string uri, CancellationToken ct) =>
        LoadCalendarAsync(currentUser, NodeAccess.Owner, uri, ct);

    private async Task<CalendarNode?> LoadCalendarAsync(User homeOwner, NodeAccess access, string uri, CancellationToken ct)
    {
        var cal = await db.Calendars.Include(c => c.Owner)
            .FirstOrDefaultAsync(c => c.OwnerId == homeOwner.Id && c.Uri == uri, ct);
        if (cal != null)
        {
            if (access != NodeAccess.Owner && cal.Kind == CalendarKind.Inbox) return null;
            return new CalendarNode(cal, null, homeOwner, paths.Calendar(homeOwner.Username, cal.Uri), access);
        }
        if (access != NodeAccess.Owner) return null;

        var groups = await GroupIdsAsync(ct);
        var share = await db.CalendarShares.Include(x => x.Calendar).ThenInclude(c => c.Owner)
            .FirstOrDefaultAsync(x => x.Uri == uri && x.Calendar.Owner.IsEnabled && x.Calendar.OwnerId != currentUser.Id &&
                                      (x.ShareeId == currentUser.Id || (x.GroupId != null && groups.Contains(x.GroupId.Value))), ct);
        return share == null
            ? null
            : new CalendarNode(share.Calendar, share, currentUser, paths.Calendar(currentUser.Username, share.Uri), ShareAccessOf(share.Access));
    }

    public async Task<AddressBookNode?> LoadAddressBookAsync(string uri, CancellationToken ct)
    {
        var book = await db.AddressBooks.Include(c => c.Owner)
            .FirstOrDefaultAsync(c => c.OwnerId == currentUser.Id && c.Uri == uri, ct);
        if (book != null) return new AddressBookNode(book, null, currentUser, paths.AddressBook(currentUser.Username, book.Uri), NodeAccess.Owner);

        var groups = await GroupIdsAsync(ct);
        var share = await db.AddressBookShares.Include(x => x.AddressBook).ThenInclude(c => c.Owner)
            .FirstOrDefaultAsync(x => x.Uri == uri && x.AddressBook.Owner.IsEnabled && x.AddressBook.OwnerId != currentUser.Id &&
                                      (x.ShareeId == currentUser.Id || (x.GroupId != null && groups.Contains(x.GroupId.Value))), ct);
        return share == null
            ? null
            : new AddressBookNode(share.AddressBook, share, currentUser, paths.AddressBook(currentUser.Username, share.Uri), ShareAccessOf(share.Access));
    }

    public async Task<List<DavNode>> ChildrenAsync(DavNode node, CancellationToken ct)
    {
        switch (node)
        {
            case RootNode:
                return
                [
                    new PrincipalsNode(paths.Principals),
                    new GroupsNode(paths.Groups),
                    new CalendarHomeNode(currentUser, paths.CalendarHome(currentUser.Username)),
                    new AddressBookHomeNode(currentUser, paths.AddressBookHome(currentUser.Username)),
                ];
            case PrincipalsNode:
                return [new PrincipalNode(currentUser, paths.Principal(currentUser.Username))];
            case PrincipalNode p when p.User.Id == currentUser.Id:
                return
                [
                    new ProxyPrincipalNode(p.User, false, paths.ProxyPrincipal(p.User.Username, false)),
                    new ProxyPrincipalNode(p.User, true, paths.ProxyPrincipal(p.User.Username, true)),
                ];
            case GroupsNode:
            {
                var mine = await GroupIdsAsync(ct);
                var groups = await db.Groups.AsNoTracking().Where(g => mine.Contains(g.Id)).ToListAsync(ct);
                return groups.Select(g => (DavNode)new GroupPrincipalNode(g, paths.Group(g.Name))).ToList();
            }
            case CalendarHomeNode home:
            {
                var owner = home.Owner;
                var own = await db.Calendars.Include(c => c.Owner).Where(c => c.OwnerId == owner.Id)
                    .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
                var result = own
                    .Where(c => home.Access == NodeAccess.Owner || c.Kind != CalendarKind.Inbox)
                    .Select(c => (DavNode)new CalendarNode(c, null, owner, paths.Calendar(owner.Username, c.Uri), home.Access))
                    .ToList();
                if (home.Access != NodeAccess.Owner) return result;

                result.Add(new OutboxNode(owner, paths.Outbox(owner.Username)));
                var groups = await GroupIdsAsync(ct);
                var shared = await db.CalendarShares.Include(x => x.Calendar).ThenInclude(c => c.Owner)
                    .Where(x => x.Calendar.Owner.IsEnabled && x.Calendar.OwnerId != owner.Id &&
                                (x.ShareeId == owner.Id || (x.GroupId != null && groups.Contains(x.GroupId.Value))))
                    .ToListAsync(ct);
                // A calendar shared both directly and via a group appears once (the direct share wins).
                foreach (var x in shared.OrderBy(x => x.GroupId != null).DistinctBy(x => x.CalendarId))
                    result.Add(new CalendarNode(x.Calendar, x, owner, paths.Calendar(owner.Username, x.Uri), ShareAccessOf(x.Access)));
                return result;
            }
            case AddressBookHomeNode:
            {
                var own = await db.AddressBooks.Include(c => c.Owner).Where(c => c.OwnerId == currentUser.Id)
                    .OrderBy(c => c.Id).ToListAsync(ct);
                var groups = await GroupIdsAsync(ct);
                var shared = await db.AddressBookShares.Include(x => x.AddressBook).ThenInclude(c => c.Owner)
                    .Where(x => x.AddressBook.Owner.IsEnabled && x.AddressBook.OwnerId != currentUser.Id &&
                                (x.ShareeId == currentUser.Id || (x.GroupId != null && groups.Contains(x.GroupId.Value))))
                    .ToListAsync(ct);
                return own.Select(c => (DavNode)new AddressBookNode(c, null, currentUser, paths.AddressBook(currentUser.Username, c.Uri), NodeAccess.Owner))
                    .Concat(shared.OrderBy(x => x.GroupId != null).DistinctBy(x => x.AddressBookId)
                        .Select(x => new AddressBookNode(x.AddressBook, x, currentUser, paths.AddressBook(currentUser.Username, x.Uri), ShareAccessOf(x.Access))))
                    .ToList();
            }
            case CalendarNode cal:
            {
                // Data is not needed for listings; it is loaded on demand.
                var objs = await db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == cal.Calendar.Id)
                    .Select(o => new CalendarObject
                    {
                        Id = o.Id, CalendarId = o.CalendarId, Uri = o.Uri, Uid = o.Uid, ComponentType = o.ComponentType,
                        ETag = o.ETag, Size = o.Size, ModifiedUtc = o.ModifiedUtc, CreatedUtc = o.CreatedUtc, ScheduleTag = o.ScheduleTag,
                        Data = null!,
                    }).ToListAsync(ct);
                return objs.Select(o => (DavNode)ObjectNode(cal, o)).ToList();
            }
            case AddressBookNode book:
            {
                var cards = await db.Cards.AsNoTracking().Where(c => c.AddressBookId == book.Book.Id)
                    .Select(c => new Card
                    {
                        Id = c.Id, AddressBookId = c.AddressBookId, Uri = c.Uri, Uid = c.Uid, ETag = c.ETag, Size = c.Size,
                        ModifiedUtc = c.ModifiedUtc, CreatedUtc = c.CreatedUtc, Data = null!,
                    }).ToListAsync(ct);
                return cards.Select(c => (DavNode)CardNode(book, c)).ToList();
            }
            default:
                return [];
        }
    }

    public async Task<string> GetDataAsync(CalendarObjectNode node, CancellationToken ct)
    {
        if (node.Object.Data != null) return node.Object.Data;
        node.Object.Data = await db.CalendarObjects.Where(o => o.Id == node.Object.Id).Select(o => o.Data).FirstAsync(ct);
        return node.Object.Data;
    }

    public async Task<string> GetDataAsync(CardNode node, CancellationToken ct)
    {
        if (node.Card.Data != null) return node.Card.Data;
        node.Card.Data = await db.Cards.Where(c => c.Id == node.Card.Id).Select(c => c.Data).FirstAsync(ct);
        return node.Card.Data;
    }
}
