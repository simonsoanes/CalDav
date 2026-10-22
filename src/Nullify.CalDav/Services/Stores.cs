using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Group = Nullify.CalDav.Data.Group;
using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Services;

public sealed class UidConflictException(string existingUri) : Exception("UID already in use")
{
    public string ExistingUri { get; } = existingUri;
}

public static partial class Names
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex SlugInvalid();

    [GeneratedRegex("^#[0-9A-Fa-f]{3,8}$")]
    private static partial Regex ColorRegex();

    public static bool IsValidUsername(string? s) => s != null && UsernameRegex().IsMatch(s);

    public static bool IsValidColor(string? s) => s != null && ColorRegex().IsMatch(s);

    /// <summary>Returns a color safe to embed in CSS.</summary>
    public static string SafeColor(string? s) => IsValidColor(s) ? s! : "#8a94a6";

    public static bool IsValidSegment(string? s) =>
        !string.IsNullOrEmpty(s) && s.Length <= 200 && s != "." && s != ".." && !s.Contains('/') && !s.Contains('\\') &&
        !s.Any(char.IsControl);

    public static string Slugify(string text, string fallback = "item")
    {
        var slug = SlugInvalid().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-', '.');
        if (slug.Length > 48) slug = slug[..48].Trim('-', '.');
        return slug.Length == 0 ? fallback : slug;
    }

    public static string ETag(string data) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..32].ToLowerInvariant() + "\"";
}

public sealed class CalendarStore(AppDbContext db, QuotaService quota)
{
    public const int MaxResourceSize = 10 * 1024 * 1024;
    public static readonly string[] SupportedComponents = ["VEVENT", "VTODO", "VJOURNAL"];

    public async Task<string> UniqueHomeUriAsync(int userId, string baseUri, CancellationToken ct = default)
    {
        var candidate = baseUri;
        for (var i = 2; ; i++)
        {
            if (!await HomeUriTakenAsync(userId, candidate, ct)) return candidate;
            candidate = $"{baseUri}-{i}";
        }
    }

    /// <summary>Names used by the scheduling inbox/outbox and push registrations.</summary>
    public static readonly string[] ReservedNames = ["inbox", "outbox", "push"];

    public async Task<bool> HomeUriTakenAsync(int userId, string uri, CancellationToken ct = default) =>
        ReservedNames.Contains(uri, StringComparer.OrdinalIgnoreCase) ||
        await db.Calendars.AnyAsync(c => c.OwnerId == userId && c.Uri == uri, ct) ||
        await db.CalendarShares.AnyAsync(s => s.Uri == uri && (s.ShareeId == userId || s.GroupId != null), ct);

    public async Task<Calendar> CreateAsync(int ownerId, string uri, string displayName, IEnumerable<string>? components = null,
        string? description = null, string? color = null, CancellationToken ct = default)
    {
        var cal = new Calendar
        {
            OwnerId = ownerId,
            Uri = uri,
            DisplayName = displayName,
            Description = description,
            Color = color,
            Components = string.Join(',', (components ?? SupportedComponents).Select(c => c.ToUpperInvariant()).Distinct()),
        };
        db.Calendars.Add(cal);
        await db.SaveChangesAsync(ct);
        return cal;
    }

    public async Task DeleteAsync(Calendar cal, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (cal.Kind == CalendarKind.Normal)
        {
            var items = await db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == cal.Id)
                .Select(o => new TrashedResource(o.Uri, o.Data)).ToListAsync(ct);
            var payload = JsonSerializer.Serialize(new TrashedCalendar(cal.Uri, cal.DisplayName, cal.Description, cal.Color,
                cal.Components, cal.TimeZone, cal.SortOrder, items));
            db.TrashItems.Add(new TrashItem
            {
                OwnerId = cal.OwnerId, Kind = TrashKind.Calendar, CollectionName = cal.DisplayName, Title = cal.DisplayName,
                Uri = cal.Uri, Payload = payload, Size = items.Sum(i => (long)i.Data.Length),
            });
            await db.SaveChangesAsync(ct);
        }
        await db.Calendars.Where(c => c.Id == cal.Id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<long> BumpAsync(Calendar cal, string uri, ChangeOperation op, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Calendars.Where(c => c.Id == cal.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SyncToken, c => c.SyncToken + 1).SetProperty(c => c.ModifiedUtc, now), ct);
        var token = await db.Calendars.Where(c => c.Id == cal.Id).Select(c => c.SyncToken).FirstAsync(ct);
        db.CalendarChanges.Add(new CalendarChange { CalendarId = cal.Id, Uri = uri, SyncToken = token, Operation = op });
        cal.SyncToken = token;
        return token;
    }

    /// <summary>
    /// Creates or replaces a calendar object. Throws <see cref="UidConflictException"/> on duplicate UIDs and
    /// <see cref="QuotaExceededException"/> when the owner's quota would be exceeded.
    /// </summary>
    public async Task<CalendarObject> PutAsync(Calendar cal, string uri, string data, CalendarObjectInfo info, CalendarObject? existing,
        bool preserveScheduleTag = false, CancellationToken ct = default)
    {
        var newSize = Encoding.UTF8.GetByteCount(data);
        if (cal.Kind is CalendarKind.Normal or CalendarKind.Inbox)
            await quota.EnsureAsync(db, cal.OwnerId, newSize - (existing?.Size ?? 0), ct);

        // Scheduling inboxes legitimately hold several messages for the same UID.
        if (!string.IsNullOrEmpty(info.Uid) && cal.Kind != CalendarKind.Inbox)
        {
            var clash = await db.CalendarObjects
                .Where(o => o.CalendarId == cal.Id && o.Uid == info.Uid && o.Uri != uri)
                .Select(o => o.Uri).FirstOrDefaultAsync(ct);
            if (clash != null) throw new UidConflictException(clash);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var obj = existing ?? new CalendarObject { CalendarId = cal.Id, Uri = uri, CreatedUtc = now };
        obj.Data = data;
        obj.Uid = info.Uid;
        obj.ComponentType = info.ComponentType;
        obj.ETag = Names.ETag(data);
        obj.Size = newSize;
        obj.FirstOccurrence = info.FirstOccurrence;
        obj.LastOccurrence = info.LastOccurrence;
        obj.ModifiedUtc = now;
        if (!preserveScheduleTag || obj.ScheduleTag == null) obj.ScheduleTag = "\"" + Guid.NewGuid().ToString("N")[..16] + "\"";
        if (existing == null) db.CalendarObjects.Add(obj);
        else db.CalendarObjects.Update(obj);
        await db.SaveChangesAsync(ct);
        await BumpAsync(cal, uri, existing == null ? ChangeOperation.Added : ChangeOperation.Modified, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return obj;
    }

    public async Task DeleteObjectAsync(Calendar cal, CalendarObject obj, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (cal.Kind == CalendarKind.Normal)
        {
            var data = obj.Data ?? await db.CalendarObjects.Where(o => o.Id == obj.Id).Select(o => o.Data).FirstAsync(ct);
            db.TrashItems.Add(new TrashItem
            {
                OwnerId = cal.OwnerId, Kind = TrashKind.CalendarObject, CollectionId = cal.Id, CollectionName = cal.DisplayName,
                Title = TrashService.TitleOf(data, card: false), Uri = obj.Uri, Payload = data, Size = data.Length,
            });
        }
        await db.CalendarObjects.Where(o => o.Id == obj.Id).ExecuteDeleteAsync(ct);
        await BumpAsync(cal, obj.Uri, ChangeOperation.Deleted, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Moves an object to another (or the same) calendar under a new name.</summary>
    public async Task MoveObjectAsync(Calendar source, CalendarObject obj, Calendar target, string targetUri,
        CalendarObject? overwritten, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(obj.Uid) && target.Id != source.Id)
        {
            var clash = await db.CalendarObjects
                .Where(o => o.CalendarId == target.Id && o.Uid == obj.Uid && o.Uri != targetUri)
                .Select(o => o.Uri).FirstOrDefaultAsync(ct);
            if (clash != null) throw new UidConflictException(clash);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (overwritten != null)
            await db.CalendarObjects.Where(o => o.Id == overwritten.Id).ExecuteDeleteAsync(ct);
        var oldUri = obj.Uri;
        await db.CalendarObjects.Where(o => o.Id == obj.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.CalendarId, target.Id)
            .SetProperty(o => o.Uri, targetUri)
            .SetProperty(o => o.ModifiedUtc, DateTime.UtcNow), ct);
        await BumpAsync(source, oldUri, ChangeOperation.Deleted, ct);
        await BumpAsync(target, targetUri, overwritten != null ? ChangeOperation.Modified : ChangeOperation.Added, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static readonly HashSet<string> BusyOnlyProperties =
        ["UID", "DTSTAMP", "DTSTART", "DTEND", "DURATION", "RRULE", "RDATE", "EXDATE", "RECURRENCE-ID", "TRANSP", "STATUS", "SEQUENCE"];

    /// <summary>Exports the calendar as a single iCalendar stream, optionally reduced to busy times only.</summary>
    public async Task<string> ExportAsync(Calendar cal, CancellationToken ct = default, bool busyOnly = false)
    {
        var root = new Component("VCALENDAR");
        root.Properties.Add(new ContentLine("VERSION", "2.0"));
        root.Properties.Add(new ContentLine("PRODID", "-//Nullify//Nullify.CalDav//EN"));
        root.Properties.Add(new ContentLine("CALSCALE", "GREGORIAN"));
        root.Properties.Add(new ContentLine("X-WR-CALNAME", ContentLine.EscapeText(cal.DisplayName)));
        var timezones = new Dictionary<string, Component>(StringComparer.OrdinalIgnoreCase);
        var items = new List<Component>();

        var datas = await db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == cal.Id)
            .OrderBy(o => o.Id).Select(o => o.Data).ToListAsync(ct);
        foreach (var data in datas)
        {
            Component parsed;
            try { parsed = CalendarObjectInfo.ParseCalendar(data); }
            catch (InvalidObjectException) { continue; }
            foreach (var c in parsed.Children)
            {
                if (c.Name == "VTIMEZONE")
                {
                    var id = c.Value("TZID");
                    if (id != null) timezones.TryAdd(id, c);
                }
                else if (!busyOnly) items.Add(c);
                else if (c.Name == "VEVENT" && !string.Equals(c.Value("CLASS"), "CONFIDENTIAL-HIDDEN", StringComparison.OrdinalIgnoreCase))
                {
                    var busy = new Component("VEVENT");
                    busy.Properties.AddRange(c.Properties.Where(p => BusyOnlyProperties.Contains(p.Name)));
                    busy.Properties.Add(new ContentLine("SUMMARY", "Busy"));
                    busy.Properties.Add(new ContentLine("CLASS", "PRIVATE"));
                    items.Add(busy);
                }
            }
        }
        root.Children.AddRange(timezones.Values);
        root.Children.AddRange(items);
        return ContentWriter.Write(root);
    }

    public sealed record ImportResult(int Imported, int Updated, int Skipped, List<string> Errors);

    /// <summary>Imports an iCalendar file, splitting it into one resource per UID.</summary>
    public async Task<ImportResult> ImportAsync(Calendar cal, string text, CancellationToken ct = default)
    {
        var errors = new List<string>();
        List<Component> roots;
        try { roots = ContentReader.Parse(text); }
        catch (ContentParseException ex) { return new ImportResult(0, 0, 0, [ex.Message]); }

        int imported = 0, updated = 0, skipped = 0;
        var allowed = cal.ComponentList;
        foreach (var vcal in roots.Where(r => r.Name == "VCALENDAR"))
        {
            var timezones = vcal.ChildrenNamed("VTIMEZONE").ToList();
            var groups = vcal.Children
                .Where(c => CalendarObjectInfo.ObjectComponents.Contains(c.Name) && c.Name != "VFREEBUSY")
                .GroupBy(c => (c.Name, Uid: c.Value("UID")?.Trim() ?? Guid.NewGuid().ToString()));

            foreach (var group in groups)
            {
                if (!allowed.Contains(group.Key.Name)) { skipped++; continue; }
                var obj = new Component("VCALENDAR");
                obj.Properties.Add(new ContentLine("VERSION", "2.0"));
                obj.Properties.Add(new ContentLine("PRODID", vcal.Value("PRODID") ?? "-//Nullify//Nullify.CalDav//EN"));
                var usedTz = group.SelectMany(AllTzIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
                obj.Children.AddRange(timezones.Where(t => usedTz.Contains(t.Value("TZID") ?? "")));
                foreach (var c in group)
                {
                    if (c.Property("UID") == null) c.Properties.Insert(0, new ContentLine("UID", group.Key.Uid));
                    obj.Children.Add(c);
                }

                var data = ContentWriter.Write(obj);
                try
                {
                    var info = CalendarObjectInfo.Analyze(data);
                    var existing = await db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Id && o.Uid == info.Uid, ct);
                    var uri = existing?.Uri ?? SafeObjectName(info.Uid) + ".ics";
                    if (existing == null && await db.CalendarObjects.AnyAsync(o => o.CalendarId == cal.Id && o.Uri == uri, ct))
                        uri = Guid.NewGuid().ToString("N") + ".ics";
                    await PutAsync(cal, uri, data, info, existing, ct: ct);
                    if (existing == null) imported++; else updated++;
                }
                catch (Exception ex) when (ex is InvalidObjectException or UidConflictException or QuotaExceededException)
                {
                    skipped++;
                    errors.Add($"{group.Key.Uid}: {ex.Message}");
                }
            }
        }
        return new ImportResult(imported, updated, skipped, errors);
    }

    private static IEnumerable<string> AllTzIds(Component c)
    {
        foreach (var p in c.Properties)
            if (p.GetParameter("TZID") is { } tz) yield return tz;
        foreach (var child in c.Children)
        foreach (var tz in AllTzIds(child))
            yield return tz;
    }

    public static string SafeObjectName(string uid)
    {
        var ok = uid.Length is > 0 and <= 120 && uid.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@');
        return ok ? uid : PasswordService.Sha256(uid)[..32].ToLowerInvariant();
    }

    /// <summary>Shares with every member of a group. Group shares use one path segment for all members.</summary>
    public async Task<CalendarShare> ShareWithGroupAsync(Calendar cal, Group group, ShareAccess access, CancellationToken ct = default)
    {
        var share = await db.CalendarShares.FirstOrDefaultAsync(s => s.CalendarId == cal.Id && s.GroupId == group.Id, ct);
        if (share == null)
        {
            var owner = await db.Users.Where(u => u.Id == cal.OwnerId).Select(u => u.Username).FirstAsync(ct);
            var baseUri = Names.Slugify($"{group.Name}-{owner}-{cal.Uri}", "group");
            var uri = baseUri;
            for (var i = 2; await db.Calendars.AnyAsync(c => c.Uri == uri, ct) || await db.CalendarShares.AnyAsync(s => s.Uri == uri, ct); i++)
                uri = $"{baseUri}-{i}";
            share = new CalendarShare { CalendarId = cal.Id, GroupId = group.Id, Uri = uri };
            db.CalendarShares.Add(share);
        }
        share.Access = access;
        await db.SaveChangesAsync(ct);
        return share;
    }

    public async Task<CalendarShare> ShareAsync(Calendar cal, User sharee, ShareAccess access, CancellationToken ct = default)
    {
        var share = await db.CalendarShares.FirstOrDefaultAsync(s => s.CalendarId == cal.Id && s.ShareeId == sharee.Id, ct);
        if (share != null)
        {
            share.Access = access;
        }
        else
        {
            var owner = await db.Users.Where(u => u.Id == cal.OwnerId).Select(u => u.Username).FirstAsync(ct);
            var uri = await UniqueHomeUriAsync(sharee.Id, Names.Slugify(owner + "-" + cal.Uri, "shared"), ct);
            share = new CalendarShare { CalendarId = cal.Id, ShareeId = sharee.Id, Access = access, Uri = uri };
            db.CalendarShares.Add(share);
        }
        await db.SaveChangesAsync(ct);
        return share;
    }
}

public sealed class AddressBookStore(AppDbContext db, QuotaService quota)
{
    public const int MaxResourceSize = 5 * 1024 * 1024;

    public async Task<bool> HomeUriTakenAsync(int userId, string uri, CancellationToken ct = default) =>
        CalendarStore.ReservedNames.Contains(uri, StringComparer.OrdinalIgnoreCase) ||
        await db.AddressBooks.AnyAsync(c => c.OwnerId == userId && c.Uri == uri, ct) ||
        await db.AddressBookShares.AnyAsync(s => s.Uri == uri && (s.ShareeId == userId || s.GroupId != null), ct);

    public async Task<string> UniqueHomeUriAsync(int userId, string baseUri, CancellationToken ct = default)
    {
        var candidate = baseUri;
        for (var i = 2; await HomeUriTakenAsync(userId, candidate, ct); i++) candidate = $"{baseUri}-{i}";
        return candidate;
    }

    public async Task<AddressBook> CreateAsync(int ownerId, string uri, string displayName, string? description = null,
        CancellationToken ct = default)
    {
        var book = new AddressBook { OwnerId = ownerId, Uri = uri, DisplayName = displayName, Description = description };
        db.AddressBooks.Add(book);
        await db.SaveChangesAsync(ct);
        return book;
    }

    public async Task DeleteAsync(AddressBook book, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (book.Kind == AddressBookKind.Normal)
        {
            var items = await db.Cards.AsNoTracking().Where(c => c.AddressBookId == book.Id)
                .Select(c => new TrashedResource(c.Uri, c.Data)).ToListAsync(ct);
            db.TrashItems.Add(new TrashItem
            {
                OwnerId = book.OwnerId, Kind = TrashKind.AddressBook, CollectionName = book.DisplayName, Title = book.DisplayName,
                Uri = book.Uri, Payload = JsonSerializer.Serialize(new TrashedAddressBook(book.Uri, book.DisplayName, book.Description, items)),
                Size = items.Sum(i => (long)i.Data.Length),
            });
            await db.SaveChangesAsync(ct);
        }
        await db.AddressBooks.Where(b => b.Id == book.Id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task BumpAsync(AddressBook book, string uri, ChangeOperation op, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.AddressBooks.Where(b => b.Id == book.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SyncToken, b => b.SyncToken + 1).SetProperty(b => b.ModifiedUtc, now), ct);
        var token = await db.AddressBooks.Where(b => b.Id == book.Id).Select(b => b.SyncToken).FirstAsync(ct);
        db.CardChanges.Add(new CardChange { AddressBookId = book.Id, Uri = uri, SyncToken = token, Operation = op });
        book.SyncToken = token;
    }

    public async Task<Card> PutAsync(AddressBook book, string uri, string data, CardInfo info, Card? existing, CancellationToken ct = default)
    {
        var newSize = Encoding.UTF8.GetByteCount(data);
        if (book.Kind == AddressBookKind.Normal)
            await quota.EnsureAsync(db, book.OwnerId, newSize - (existing?.Size ?? 0), ct);
        var clash = await db.Cards
            .Where(c => c.AddressBookId == book.Id && c.Uid == info.Uid && c.Uri != uri)
            .Select(c => c.Uri).FirstOrDefaultAsync(ct);
        if (clash != null) throw new UidConflictException(clash);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var card = existing ?? new Card { AddressBookId = book.Id, Uri = uri, CreatedUtc = now };
        card.Data = data;
        card.Uid = info.Uid;
        card.FormattedName = info.FormattedName;
        card.ETag = Names.ETag(data);
        card.Size = newSize;
        card.ModifiedUtc = now;
        if (existing == null) db.Cards.Add(card);
        else db.Cards.Update(card);
        await db.SaveChangesAsync(ct);
        await BumpAsync(book, uri, existing == null ? ChangeOperation.Added : ChangeOperation.Modified, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return card;
    }

    public async Task DeleteCardAsync(AddressBook book, Card card, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (book.Kind == AddressBookKind.Normal)
        {
            var data = card.Data ?? await db.Cards.Where(c => c.Id == card.Id).Select(c => c.Data).FirstAsync(ct);
            db.TrashItems.Add(new TrashItem
            {
                OwnerId = book.OwnerId, Kind = TrashKind.Card, CollectionId = book.Id, CollectionName = book.DisplayName,
                Title = TrashService.TitleOf(data, card: true), Uri = card.Uri, Payload = data, Size = data.Length,
            });
        }
        await db.Cards.Where(c => c.Id == card.Id).ExecuteDeleteAsync(ct);
        await BumpAsync(book, card.Uri, ChangeOperation.Deleted, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task MoveCardAsync(AddressBook source, Card card, AddressBook target, string targetUri, Card? overwritten,
        CancellationToken ct = default)
    {
        if (target.Id != source.Id)
        {
            var clash = await db.Cards.Where(c => c.AddressBookId == target.Id && c.Uid == card.Uid && c.Uri != targetUri)
                .Select(c => c.Uri).FirstOrDefaultAsync(ct);
            if (clash != null) throw new UidConflictException(clash);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (overwritten != null) await db.Cards.Where(c => c.Id == overwritten.Id).ExecuteDeleteAsync(ct);
        var oldUri = card.Uri;
        await db.Cards.Where(c => c.Id == card.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.AddressBookId, target.Id)
            .SetProperty(c => c.Uri, targetUri)
            .SetProperty(c => c.ModifiedUtc, DateTime.UtcNow), ct);
        await BumpAsync(source, oldUri, ChangeOperation.Deleted, ct);
        await BumpAsync(target, targetUri, overwritten != null ? ChangeOperation.Modified : ChangeOperation.Added, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<string> ExportAsync(AddressBook book, CancellationToken ct = default)
    {
        var datas = await db.Cards.AsNoTracking().Where(c => c.AddressBookId == book.Id).OrderBy(c => c.Id)
            .Select(c => c.Data).ToListAsync(ct);
        var sb = new StringBuilder();
        foreach (var d in datas)
        {
            sb.Append(d.TrimEnd('\r', '\n'));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public async Task<CalendarStore.ImportResult> ImportAsync(AddressBook book, string text, CancellationToken ct = default)
    {
        List<Component> roots;
        try { roots = ContentReader.Parse(text); }
        catch (ContentParseException ex) { return new CalendarStore.ImportResult(0, 0, 0, [ex.Message]); }

        int imported = 0, updated = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var vcard in roots.Where(r => r.Name == "VCARD"))
        {
            if (vcard.Property("UID") == null)
                vcard.Properties.Insert(Math.Min(1, vcard.Properties.Count), new ContentLine("UID", Guid.NewGuid().ToString()));
            var data = ContentWriter.Write(vcard);
            try
            {
                var info = CardInfo.Analyze(data, Guid.NewGuid().ToString());
                var existing = await db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Id && c.Uid == info.Uid, ct);
                var uri = existing?.Uri ?? CalendarStore.SafeObjectName(info.Uid) + ".vcf";
                if (existing == null && await db.Cards.AnyAsync(c => c.AddressBookId == book.Id && c.Uri == uri, ct))
                    uri = Guid.NewGuid().ToString("N") + ".vcf";
                await PutAsync(book, uri, data, info, existing, ct);
                if (existing == null) imported++; else updated++;
            }
            catch (Exception ex) when (ex is InvalidObjectException or UidConflictException or QuotaExceededException)
            {
                skipped++;
                errors.Add(ex.Message);
            }
        }
        return new CalendarStore.ImportResult(imported, updated, skipped, errors);
    }

    public async Task<AddressBookShare> ShareWithGroupAsync(AddressBook book, Group group, ShareAccess access, CancellationToken ct = default)
    {
        var share = await db.AddressBookShares.FirstOrDefaultAsync(s => s.AddressBookId == book.Id && s.GroupId == group.Id, ct);
        if (share == null)
        {
            var owner = await db.Users.Where(u => u.Id == book.OwnerId).Select(u => u.Username).FirstAsync(ct);
            var baseUri = Names.Slugify($"{group.Name}-{owner}-{book.Uri}", "group");
            var uri = baseUri;
            for (var i = 2; await db.AddressBooks.AnyAsync(b => b.Uri == uri, ct) || await db.AddressBookShares.AnyAsync(s => s.Uri == uri, ct); i++)
                uri = $"{baseUri}-{i}";
            share = new AddressBookShare { AddressBookId = book.Id, GroupId = group.Id, Uri = uri };
            db.AddressBookShares.Add(share);
        }
        share.Access = access;
        await db.SaveChangesAsync(ct);
        return share;
    }

    public async Task<AddressBookShare> ShareAsync(AddressBook book, User sharee, ShareAccess access, CancellationToken ct = default)
    {
        var share = await db.AddressBookShares.FirstOrDefaultAsync(s => s.AddressBookId == book.Id && s.ShareeId == sharee.Id, ct);
        if (share != null)
        {
            share.Access = access;
        }
        else
        {
            var owner = await db.Users.Where(u => u.Id == book.OwnerId).Select(u => u.Username).FirstAsync(ct);
            var uri = await UniqueHomeUriAsync(sharee.Id, Names.Slugify(owner + "-" + book.Uri, "shared"), ct);
            share = new AddressBookShare { AddressBookId = book.Id, ShareeId = sharee.Id, Access = access, Uri = uri };
            db.AddressBookShares.Add(share);
        }
        await db.SaveChangesAsync(ct);
        return share;
    }
}

public sealed class UserService(AppDbContext db, PasswordService passwords, DavCredentialValidator credentials,
    SystemCollectionsService system, CollectionChangeNotifier notifier)
{
    public async Task<User> CreateAsync(string username, string? displayName, string? email, string? password, bool isAdmin,
        string? externalSubject = null, CancellationToken ct = default)
    {
        var user = new User
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            IsAdmin = isAdmin,
            ExternalSubject = externalSubject,
        };
        if (!string.IsNullOrEmpty(password)) user.PasswordHash = passwords.Hash(user, password);
        user.Calendars.Add(new Calendar { Uri = "default", DisplayName = "Personal", Color = "#3B82F6", Components = "VEVENT,VTODO,VJOURNAL" });
        user.AddressBooks.Add(new AddressBook { Uri = "default", DisplayName = "Contacts" });
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await system.EnsureForUserAsync(db, user.Id, ct);
        return user;
    }

    public async Task SetPasswordAsync(User user, string? password, CancellationToken ct = default)
    {
        user.PasswordHash = string.IsNullOrEmpty(password) ? null : passwords.Hash(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
        credentials.InvalidateAll();
    }

    public async Task TouchStampAsync(User user, CancellationToken ct = default)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
        credentials.InvalidateAll();
    }

    public async Task<bool> IsLastAdminAsync(User user, CancellationToken ct = default) =>
        user.IsAdmin && !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsAdmin && u.IsEnabled, ct);

    public async Task DeleteAsync(User user, CancellationToken ct = default)
    {
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(ct);
        credentials.InvalidateAll();
        notifier.RaiseUsersChanged();
    }
}
