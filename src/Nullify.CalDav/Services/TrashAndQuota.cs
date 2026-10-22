// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Services;

public sealed class QuotaExceededException(long limit, long used) : Exception("Storage quota exceeded")
{
    public long Limit { get; } = limit;
    public long Used { get; } = used;
}

/// <summary>Per-user storage quotas (RFC 4331). System-generated collections do not count.</summary>
public sealed class QuotaService(SettingsService settings)
{
    public async Task<long?> LimitAsync(AppDbContext db, int userId, CancellationToken ct = default)
    {
        var own = await db.Users.Where(u => u.Id == userId).Select(u => u.QuotaBytes).FirstOrDefaultAsync(ct);
        var limit = own ?? settings.Current.DefaultQuotaMb * 1024 * 1024;
        return limit > 0 ? limit : null;
    }

    public async Task<long> UsedAsync(AppDbContext db, int userId, CancellationToken ct = default)
    {
        var cal = await db.CalendarObjects
            .Where(o => o.Calendar.OwnerId == userId && (o.Calendar.Kind == CalendarKind.Normal || o.Calendar.Kind == CalendarKind.Inbox))
            .SumAsync(o => (long)o.Size, ct);
        var cards = await db.Cards
            .Where(c => c.AddressBook.OwnerId == userId && c.AddressBook.Kind == AddressBookKind.Normal)
            .SumAsync(c => (long)c.Size, ct);
        return cal + cards;
    }

    /// <summary>Throws <see cref="QuotaExceededException"/> when adding <paramref name="additional"/> bytes would exceed the quota.</summary>
    public async Task EnsureAsync(AppDbContext db, int userId, long additional, CancellationToken ct = default)
    {
        if (additional <= 0) return;
        if (await LimitAsync(db, userId, ct) is not { } limit) return;
        var used = await UsedAsync(db, userId, ct);
        if (used + additional > limit) throw new QuotaExceededException(limit, used);
    }
}

public sealed record TrashedResource(string Uri, string Data);

public sealed record TrashedCalendar(string Uri, string DisplayName, string? Description, string? Color, string Components,
    string? TimeZone, int SortOrder, List<TrashedResource> Items);

public sealed record TrashedAddressBook(string Uri, string DisplayName, string? Description, List<TrashedResource> Items);

/// <summary>Restores and purges deleted items.</summary>
public sealed class TrashService(AppDbContext db, CalendarStore calendars, AddressBookStore books)
{
    public static string TitleOf(string data, bool card)
    {
        try
        {
            var root = ContentReader.Parse(data).FirstOrDefault();
            if (root == null) return "";
            if (card) return root.Property("FN")?.TextValue ?? "";
            var comp = root.Children.FirstOrDefault(c => CalendarObjectInfo.ObjectComponents.Contains(c.Name));
            return comp?.Property("SUMMARY")?.TextValue ?? comp?.Name ?? "";
        }
        catch (ContentParseException) { return ""; }
    }

    public async Task<int> PurgeExpiredAsync(int retentionDays, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, retentionDays));
        return await db.TrashItems.Where(t => t.DeletedUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>Restores a trashed item. Returns a message describing the outcome.</summary>
    public async Task<(bool Ok, string Message)> RestoreAsync(long id, int ownerId, CancellationToken ct = default)
    {
        var item = await db.TrashItems.FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == ownerId, ct);
        if (item == null) return (false, "Item not found.");

        switch (item.Kind)
        {
            case TrashKind.CalendarObject:
            {
                var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == item.CollectionId && c.OwnerId == ownerId, ct);
                if (cal == null) return (false, $"The calendar \"{item.CollectionName}\" no longer exists. Restore it first.");
                var info = CalendarObjectInfo.Analyze(item.Payload);
                if (await db.CalendarObjects.AnyAsync(o => o.CalendarId == cal.Id && o.Uid == info.Uid, ct))
                    return (false, "An item with the same UID already exists in that calendar.");
                var uri = await db.CalendarObjects.AnyAsync(o => o.CalendarId == cal.Id && o.Uri == item.Uri, ct)
                    ? Guid.NewGuid().ToString("N") + ".ics" : item.Uri;
                await calendars.PutAsync(cal, uri, item.Payload, info, null, ct: ct);
                break;
            }
            case TrashKind.Card:
            {
                var book = await db.AddressBooks.FirstOrDefaultAsync(b => b.Id == item.CollectionId && b.OwnerId == ownerId, ct);
                if (book == null) return (false, $"The address book \"{item.CollectionName}\" no longer exists. Restore it first.");
                var info = CardInfo.Analyze(item.Payload, Path.GetFileNameWithoutExtension(item.Uri));
                if (await db.Cards.AnyAsync(c => c.AddressBookId == book.Id && c.Uid == info.Uid, ct))
                    return (false, "A contact with the same UID already exists in that address book.");
                var uri = await db.Cards.AnyAsync(c => c.AddressBookId == book.Id && c.Uri == item.Uri, ct)
                    ? Guid.NewGuid().ToString("N") + ".vcf" : item.Uri;
                await books.PutAsync(book, uri, item.Payload, info, null, ct);
                break;
            }
            case TrashKind.Calendar:
            {
                var t = JsonSerializer.Deserialize<TrashedCalendar>(item.Payload)!;
                var uri = await calendars.UniqueHomeUriAsync(ownerId, t.Uri, ct);
                var cal = await calendars.CreateAsync(ownerId, uri, t.DisplayName, t.Components.Split(','), t.Description, t.Color, ct);
                cal.TimeZone = t.TimeZone;
                cal.SortOrder = t.SortOrder;
                await db.SaveChangesAsync(ct);
                foreach (var r in t.Items)
                {
                    try { await calendars.PutAsync(cal, r.Uri, r.Data, CalendarObjectInfo.Analyze(r.Data), null, ct: ct); }
                    catch (Exception ex) when (ex is InvalidObjectException or UidConflictException) { }
                }
                break;
            }
            case TrashKind.AddressBook:
            {
                var t = JsonSerializer.Deserialize<TrashedAddressBook>(item.Payload)!;
                var uri = await books.UniqueHomeUriAsync(ownerId, t.Uri, ct);
                var book = await books.CreateAsync(ownerId, uri, t.DisplayName, t.Description, ct);
                foreach (var r in t.Items)
                {
                    try { await books.PutAsync(book, r.Uri, r.Data, CardInfo.Analyze(r.Data, Path.GetFileNameWithoutExtension(r.Uri)), null, ct); }
                    catch (Exception ex) when (ex is InvalidObjectException or UidConflictException) { }
                }
                break;
            }
        }

        db.TrashItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return (true, $"Restored \"{(string.IsNullOrEmpty(item.Title) ? item.Uri : item.Title)}\".");
    }
}
