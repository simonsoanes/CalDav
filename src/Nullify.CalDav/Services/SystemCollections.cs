using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Calendar = Nullify.CalDav.Data.Calendar;
using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Services;

/// <summary>Creates and maintains generated per-user collections (scheduling inbox, birthdays, directory).</summary>
public sealed class SystemCollectionsService(IServiceScopeFactory scopes, SettingsService settings, WriteGate gate, ILogger<SystemCollectionsService> log)
{
    private volatile bool _directoryDirty = true;

    public void RequestDirectoryRebuild() => _directoryDirty = true;

    public bool TakeDirectoryDirty()
    {
        if (!_directoryDirty) return false;
        _directoryDirty = false;
        return true;
    }

    /// <summary>Ensures every user has an inbox (and birthday/directory collections when enabled).</summary>
    public async Task EnsureAllAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userIds = await db.Users.Select(u => u.Id).ToListAsync(ct);
        using (await gate.EnterAsync(ct))
        {
            foreach (var id in userIds) await EnsureForUserAsync(db, id, ct);
        }
    }

    public async Task EnsureForUserAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var s = settings.Current;
        var calendars = await db.Calendars.Where(c => c.OwnerId == userId).Select(c => new { c.Kind, c.Uri }).ToListAsync(ct);
        if (!calendars.Any(c => c.Kind == CalendarKind.Inbox) && !calendars.Any(c => c.Uri == "inbox"))
            db.Calendars.Add(new Calendar { OwnerId = userId, Uri = "inbox", DisplayName = "Inbox", Kind = CalendarKind.Inbox, Components = "VEVENT,VTODO" });

        if (s.BirthdaysEnabled && !calendars.Any(c => c.Kind == CalendarKind.Birthdays))
        {
            var uri = calendars.Any(c => c.Uri == "birthdays") ? "birthdays-" + userId : "birthdays";
            db.Calendars.Add(new Calendar
            {
                OwnerId = userId, Uri = uri, DisplayName = "Birthdays", Kind = CalendarKind.Birthdays, Components = "VEVENT", Color = "#F59E0B",
                Description = "Generated from your contacts",
            });
        }
        else if (!s.BirthdaysEnabled)
        {
            await db.Calendars.Where(c => c.OwnerId == userId && c.Kind == CalendarKind.Birthdays).ExecuteDeleteAsync(ct);
        }

        var books = await db.AddressBooks.Where(b => b.OwnerId == userId).Select(b => new { b.Kind, b.Uri }).ToListAsync(ct);
        if (s.DirectoryEnabled && !books.Any(b => b.Kind == AddressBookKind.Directory))
        {
            var uri = books.Any(b => b.Uri == "directory") ? "directory-" + userId : "directory";
            db.AddressBooks.Add(new AddressBook
            {
                OwnerId = userId, Uri = uri, DisplayName = "Directory", Kind = AddressBookKind.Directory,
                Description = "Everyone on this server",
            });
            _directoryDirty = true;
        }
        else if (!s.DirectoryEnabled)
        {
            await db.AddressBooks.Where(b => b.OwnerId == userId && b.Kind == AddressBookKind.Directory).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Synchronises a generated collection's contents with the desired resources (uri → data).</summary>
    public static async Task SyncCalendarAsync(AppDbContext db, CalendarStore store, Calendar cal, Dictionary<string, string> desired, CancellationToken ct)
    {
        var existing = await db.CalendarObjects.Where(o => o.CalendarId == cal.Id).ToListAsync(ct);
        foreach (var obj in existing.Where(o => !desired.ContainsKey(o.Uri)))
            await store.DeleteObjectAsync(cal, obj, ct);
        var byUri = existing.ToDictionary(o => o.Uri);
        foreach (var (uri, data) in desired)
        {
            byUri.TryGetValue(uri, out var current);
            if (current?.Data == data) continue;
            try { await store.PutAsync(cal, uri, data, CalendarObjectInfo.Analyze(data), current, ct: ct); }
            catch (Exception ex) when (ex is InvalidObjectException or UidConflictException) { }
        }
    }

    // ------------------------------------------------------------------ Directory

    public async Task RebuildDirectoryAsync(CancellationToken ct)
    {
        if (!settings.Current.DirectoryEnabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<AddressBookStore>();
        var users = await db.Users.AsNoTracking().Where(u => u.IsEnabled).OrderBy(u => u.Id).ToListAsync(ct);
        var desired = users.ToDictionary(u => $"user-{u.Id}.vcf", DirectoryCard);

        using (await gate.EnterAsync(ct))
        {
            var books = await db.AddressBooks.Where(b => b.Kind == AddressBookKind.Directory).ToListAsync(ct);
            foreach (var book in books)
            {
                var existing = await db.Cards.Where(c => c.AddressBookId == book.Id).ToListAsync(ct);
                foreach (var card in existing.Where(c => !desired.ContainsKey(c.Uri)))
                    await store.DeleteCardAsync(book, card, ct);
                var byUri = existing.ToDictionary(c => c.Uri);
                foreach (var (uri, data) in desired)
                {
                    byUri.TryGetValue(uri, out var current);
                    if (current?.Data == data) continue;
                    await store.PutAsync(book, uri, data, CardInfo.Analyze(data, uri), current, ct);
                }
            }
        }
        log.LogDebug("Directory rebuilt for {Count} users", users.Count);
    }

    private static string DirectoryCard(User u)
    {
        var card = new Component("VCARD");
        card.Properties.Add(new ContentLine("VERSION", "3.0"));
        card.Properties.Add(new ContentLine("UID", "nullify-user-" + u.Id));
        var display = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName;
        card.Properties.Add(new ContentLine("FN", ContentLine.EscapeText(display)));
        var parts = display.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        card.Properties.Add(new ContentLine("N", parts.Length == 2
            ? $"{ContentLine.EscapeText(parts[1])};{ContentLine.EscapeText(parts[0])};;;"
            : $"{ContentLine.EscapeText(display)};;;;"));
        card.Properties.Add(new ContentLine("NICKNAME", ContentLine.EscapeText(u.Username)));
        if (!string.IsNullOrEmpty(u.Email))
        {
            var email = new ContentLine("EMAIL", u.Email);
            email.Parameters.Add(new ContentParameter("TYPE", ["INTERNET", "WORK"]));
            card.Properties.Add(email);
        }
        card.Properties.Add(new ContentLine("CATEGORIES", "Directory"));
        return ContentWriter.Write(card);
    }
}

/// <summary>Generates each user's birthday calendar from the BDAY of contacts in their own address books.</summary>
public sealed class BirthdayService(IServiceScopeFactory scopes, SettingsService settings, WriteGate gate)
{
    public async Task OnAddressBookChangedAsync(int bookId, CancellationToken ct)
    {
        if (!settings.Current.BirthdaysEnabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.AddressBooks.Where(b => b.Id == bookId && b.Kind == AddressBookKind.Normal).Select(b => (int?)b.OwnerId).FirstOrDefaultAsync(ct);
        if (owner != null) await RebuildAsync(owner.Value, ct);
    }

    public async Task RebuildAsync(int userId, CancellationToken ct)
    {
        if (!settings.Current.BirthdaysEnabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<CalendarStore>();
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.OwnerId == userId && c.Kind == CalendarKind.Birthdays, ct);
        if (cal == null) return;

        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.AddressBook.OwnerId == userId && c.AddressBook.Kind == AddressBookKind.Normal && c.Data.Contains("BDAY"))
            .Select(c => new { c.Uid, c.Data, c.AddressBookId }).ToListAsync(ct);
        var desired = new Dictionary<string, string>();
        foreach (var c in cards)
        {
            if (BirthdayEvent(c.Uid, c.Data) is not { } ev) continue;
            desired[$"bday-{PasswordService.Sha256(c.AddressBookId + ":" + c.Uid)[..24].ToLowerInvariant()}.ics"] = ev;
        }
        using (await gate.EnterAsync(ct))
            await SystemCollectionsService.SyncCalendarAsync(db, store, cal, desired, ct);
    }

    public static string? BirthdayEvent(string cardUid, string vcard)
    {
        Component card;
        try { card = ContentReader.Parse(vcard).First(r => r.Name == "VCARD"); }
        catch (Exception ex) when (ex is ContentParseException or InvalidOperationException) { return null; }
        var bday = card.Property("BDAY");
        if (bday == null || !TryParseBirthday(bday, out var year, out var month, out var day)) return null;
        var name = card.Property("FN")?.TextValue;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var startYear = year ?? 2000;
        if (month == 2 && day == 29 && !DateTime.IsLeapYear(startYear)) startYear = 2000;
        var start = new DateTime(startYear, month, day);
        var ev = new Component("VEVENT");
        ev.Properties.Add(new ContentLine("UID", "bday-" + cardUid));
        ev.Properties.Add(new ContentLine("DTSTAMP", "20000101T000000Z"));
        ev.Properties.Add(Date("DTSTART", start));
        ev.Properties.Add(Date("DTEND", start.AddDays(1)));
        ev.Properties.Add(new ContentLine("RRULE", month == 2 && day == 29 ? "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=-1" : "FREQ=YEARLY"));
        ev.Properties.Add(new ContentLine("SUMMARY", ContentLine.EscapeText(year != null ? $"{name}'s birthday ({year})" : $"{name}'s birthday")));
        ev.Properties.Add(new ContentLine("TRANSP", "TRANSPARENT"));
        ev.Properties.Add(new ContentLine("CATEGORIES", "Birthday"));
        ev.Properties.Add(new ContentLine("CLASS", "PRIVATE"));
        var cal = new Component("VCALENDAR");
        cal.Properties.Add(new ContentLine("VERSION", "2.0"));
        cal.Properties.Add(new ContentLine("PRODID", "-//Nullify//Nullify.CalDav Birthdays//EN"));
        cal.Children.Add(ev);
        return ContentWriter.Write(cal);
    }

    private static ContentLine Date(string name, DateTime d)
    {
        var l = new ContentLine(name, d.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        l.Parameters.Add(new ContentParameter("VALUE", ["DATE"]));
        return l;
    }

    /// <summary>Parses vCard birthdays: 1980-04-15, 19800415, --04-15, --0415, with optional time and X-APPLE-OMIT-YEAR.</summary>
    public static bool TryParseBirthday(ContentLine bday, out int? year, out int month, out int day)
    {
        year = null;
        month = day = 0;
        var v = bday.Value.Trim();
        var t = v.IndexOf('T');
        if (t > 0) v = v[..t];
        string digits;
        if (v.StartsWith("--", StringComparison.Ordinal)) digits = v[2..].Replace("-", "");
        else digits = v.Replace("-", "");
        if (digits.Length == 4)
        {
            if (!int.TryParse(digits[..2], out month) || !int.TryParse(digits[2..], out day)) return false;
        }
        else if (digits.Length == 8)
        {
            if (!int.TryParse(digits[..4], out var y) || !int.TryParse(digits[4..6], out month) || !int.TryParse(digits[6..], out day)) return false;
            var omit = bday.GetParameter("X-APPLE-OMIT-YEAR");
            year = omit == y.ToString(CultureInfo.InvariantCulture) || y < 1800 ? null : y;
        }
        else return false;
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month);
    }
}

/// <summary>Fetches subscribed calendars (webcal/https iCalendar feeds) into read-only collections.</summary>
public sealed class SubscriptionService(IServiceScopeFactory scopes, SettingsService settings, OutboundHttp http, WriteGate gate, ILogger<SubscriptionService> log)
{
    private const int MaxFeedBytes = 20 * 1024 * 1024;

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[10..];
        return url;
    }

    public async Task RefreshDueAsync(CancellationToken ct)
    {
        if (!settings.Current.SubscriptionsEnabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var subs = await db.Calendars.AsNoTracking().Where(c => c.Kind == CalendarKind.Subscription)
            .Select(c => new { c.Id, c.SubscriptionLastFetchUtc, c.SubscriptionRefreshMinutes }).ToListAsync(ct);
        var minimum = settings.Current.SubscriptionMinRefreshMinutes;
        foreach (var s in subs.Where(s => s.SubscriptionLastFetchUtc == null ||
                                          s.SubscriptionLastFetchUtc.Value.AddMinutes(Math.Max(minimum, s.SubscriptionRefreshMinutes)) <= now))
            await RefreshAsync(s.Id, ct);
    }

    /// <summary>Fetches one subscription. Returns null on success or an error message.</summary>
    public async Task<string?> RefreshAsync(int calendarId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<CalendarStore>();
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId && c.Kind == CalendarKind.Subscription, ct);
        if (cal?.SubscriptionUrl == null) return "Subscription not found";

        string? error = null;
        try
        {
            var url = NormalizeUrl(cal.SubscriptionUrl);
            var client = http.Client(settings.Current.SubscriptionsAllowPrivateNetworks);
            HttpRequestMessage Request(string u)
            {
                var r = new HttpRequestMessage(HttpMethod.Get, u);
                r.Headers.Accept.ParseAdd("text/calendar");
                if (cal.SubscriptionETag != null) r.Headers.TryAddWithoutValidation("If-None-Match", cal.SubscriptionETag);
                return r;
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(Request(url), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (cal.SubscriptionUrl.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            {
                // webcal:// does not say whether the feed is served over TLS; fall back to plain HTTP.
                response = await client.SendAsync(Request("http://" + url["https://".Length..]), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            using var _ = response;
            if (response.StatusCode != HttpStatusCode.NotModified)
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new HttpRequestException("Feed is too large");
                var text = await ReadLimitedAsync(response.Content, ct);
                var desired = ParseFeed(text, cal);
                using (await gate.EnterAsync(ct))
                    await SystemCollectionsService.SyncCalendarAsync(db, store, cal, desired, ct);
                cal.SubscriptionETag = response.Headers.ETag?.ToString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidObjectException or ContentParseException or UriFormatException)
        {
            error = ex.Message;
            log.LogInformation("Refreshing subscription {Id} failed: {Message}", calendarId, ex.Message);
        }

        cal.SubscriptionLastFetchUtc = DateTime.UtcNow;
        cal.SubscriptionError = error;
        using (await gate.EnterAsync(ct)) await db.SaveChangesAsync(ct);
        return error;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxFeedBytes) throw new HttpRequestException("Feed is too large");
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>Splits a feed into one resource per UID.</summary>
    public static Dictionary<string, string> ParseFeed(string text, Calendar cal)
    {
        var roots = ContentReader.Parse(text);
        var result = new Dictionary<string, string>();
        foreach (var vcal in roots.Where(r => r.Name == "VCALENDAR"))
        {
            var timezones = vcal.ChildrenNamed("VTIMEZONE").ToList();
            foreach (var group in vcal.Children.Where(c => c.Name is "VEVENT" or "VTODO" or "VJOURNAL")
                         .GroupBy(c => c.Value("UID")?.Trim() is { Length: > 0 } uid ? uid : PasswordService.Sha256(ContentWriter.Write(c))))
            {
                if (!cal.ComponentList.Contains(group.First().Name)) continue;
                var obj = new Component("VCALENDAR");
                obj.Properties.Add(new ContentLine("VERSION", "2.0"));
                obj.Properties.Add(new ContentLine("PRODID", vcal.Value("PRODID") ?? "-//Nullify//Nullify.CalDav//EN"));
                obj.Children.AddRange(timezones);
                foreach (var c in group)
                {
                    if (c.Property("UID") == null) c.Properties.Insert(0, new ContentLine("UID", group.Key));
                    obj.Children.Add(c);
                }
                result[CalendarStore.SafeObjectName(group.Key) + ".ics"] = ContentWriter.Write(obj);
            }
        }
        return result;
    }
}

/// <summary>Periodic background work: subscriptions, directory, trash retention and push housekeeping.</summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, SettingsService settings, SystemCollectionsService system,
    SubscriptionService subscriptions, BirthdayService birthdays, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await system.EnsureAllAsync(stoppingToken);
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var id in await db.Users.Select(u => u.Id).ToListAsync(stoppingToken))
                    await birthdays.RebuildAsync(id, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Initialising system collections failed");
        }

        var lastHourly = DateTime.MinValue;
        var lastSubscriptions = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (system.TakeDirectoryDirty()) await system.RebuildDirectoryAsync(stoppingToken);
                if (DateTime.UtcNow - lastSubscriptions > TimeSpan.FromMinutes(1))
                {
                    lastSubscriptions = DateTime.UtcNow;
                    await subscriptions.RefreshDueAsync(stoppingToken);
                }
                if (DateTime.UtcNow - lastHourly > TimeSpan.FromHours(1))
                {
                    lastHourly = DateTime.UtcNow;
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var purged = await scope.ServiceProvider.GetRequiredService<TrashService>().PurgeExpiredAsync(settings.Current.TrashRetentionDays, stoppingToken);
                    var now = DateTime.UtcNow;
                    var expired = await db.PushRegistrations.Where(r => r.ExpiresUtc < now).ExecuteDeleteAsync(stoppingToken);
                    if (purged + expired > 0) log.LogInformation("Purged {Trash} trash items and {Push} expired push registrations", purged, expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Maintenance run failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
