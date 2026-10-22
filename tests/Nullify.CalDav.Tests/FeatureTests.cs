// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

/// <summary>Trash, quotas, scheduling, delegation, groups, push, subscriptions, generated collections, vCard negotiation.</summary>
[TestClass]
public sealed class FeatureTests
{
    private static Task<(HttpStatusCode Status, string Body)> Send(HttpClient c, string method, string url, string? body = null,
        params (string, string)[] headers) => DavIntegrationTests.SendAsync(c, method, url, body, headers);

    private static async Task<T> WithDbAsync<T>(Func<AppDbContext, IServiceProvider, Task<T>> action)
    {
        using var scope = (await TestHost.GetAsync()).Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider);
    }

    private static async Task<User> CreateUserAsync(string name)
    {
        return await WithDbAsync(async (db, sp) =>
            await db.Users.FirstOrDefaultAsync(u => u.Username == name)
            ?? await sp.GetRequiredService<UserService>().CreateAsync(name, name.ToUpperInvariant(), name + "@example.com", name + "-password", false));
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, int seconds = 15) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } result) return result;
            await Task.Delay(200);
        }
        return null;
    }

    private static string Event(string uid, string extra = "", string start = "20261110T100000Z", string end = "20261110T110000Z") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20260101T000000Z
        DTSTART:{start}
        DTEND:{end}
        SUMMARY:Event {uid}
        {extra}
        END:VEVENT
        END:VCALENDAR
        """.Replace("\n\n", "\n");

    // ------------------------------------------------------------------ Trash

    [TestMethod]
    public async Task DeletedItemsGoToTrashAndCanBeRestored()
    {
        var u = await CreateUserAsync("trashy");
        var c = await TestHost.ClientAsync("trashy", "trashy-password");
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/calendars/trashy/default/t1.ics", Event("trash-1"))).Status);
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(c, "DELETE", "/dav/calendars/trashy/default/t1.ics")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Send(c, "GET", "/dav/calendars/trashy/default/t1.ics")).Status);

        var restored = await WithDbAsync(async (db, sp) =>
        {
            var item = await db.TrashItems.SingleAsync(t => t.OwnerId == u.Id && t.Kind == TrashKind.CalendarObject);
            Assert.AreEqual("Event trash-1", item.Title);
            return await sp.GetRequiredService<TrashService>().RestoreAsync(item.Id, u.Id);
        });
        Assert.IsTrue(restored.Ok, restored.Message);
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "GET", "/dav/calendars/trashy/default/t1.ics")).Status);

        // Whole collections are restorable too.
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "MKCALENDAR", "/dav/calendars/trashy/doomed/")).Status);
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/calendars/trashy/doomed/x.ics", Event("trash-2"))).Status);
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(c, "DELETE", "/dav/calendars/trashy/doomed/")).Status);
        var restoredCal = await WithDbAsync(async (db, sp) =>
        {
            var item = await db.TrashItems.SingleAsync(t => t.OwnerId == u.Id && t.Kind == TrashKind.Calendar);
            return await sp.GetRequiredService<TrashService>().RestoreAsync(item.Id, u.Id);
        });
        Assert.IsTrue(restoredCal.Ok, restoredCal.Message);
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "GET", "/dav/calendars/trashy/doomed/x.ics")).Status);
    }

    // ------------------------------------------------------------------ Quota

    [TestMethod]
    public async Task QuotaIsEnforcedAndReported()
    {
        var u = await CreateUserAsync("quota");
        await WithDbAsync(async (db, _) =>
        {
            await db.Users.Where(x => x.Id == u.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.QuotaBytes, 2000));
            return 0;
        });
        var c = await TestHost.ClientAsync("quota", "quota-password");
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/calendars/quota/default/small.ics", Event("q-1"))).Status);
        var big = Event("q-2", "DESCRIPTION:" + new string('x', 3000));
        var (status, body) = await Send(c, "PUT", "/dav/calendars/quota/default/big.ics", big);
        Assert.AreEqual(HttpStatusCode.InsufficientStorage, status);
        StringAssert.Contains(body, "quota-not-exceeded");

        (_, body) = await Send(c, "PROPFIND", "/dav/calendars/quota/default/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:quota-used-bytes/><d:quota-available-bytes/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(body, "quota-available-bytes>");
    }

    // ------------------------------------------------------------------ Scheduling

    [TestMethod]
    public async Task ImplicitSchedulingDeliversRepliesAndCancels()
    {
        await CreateUserAsync("organizer");
        await CreateUserAsync("attendee");
        var org = await TestHost.ClientAsync("organizer", "organizer-password");
        var att = await TestHost.ClientAsync("attendee", "attendee-password");

        var invite = Event("meeting-1",
            "ORGANIZER;CN=Organizer:mailto:organizer@example.com\nATTENDEE;PARTSTAT=ACCEPTED:mailto:organizer@example.com\n" +
            "ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:attendee@example.com\nATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:outsider@elsewhere.test");
        var put = await org.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/dav/calendars/organizer/default/meeting.ics")
        {
            Content = new StringContent(invite, Encoding.UTF8, "text/calendar"),
        });
        Assert.AreEqual(HttpStatusCode.Created, put.StatusCode);
        Assert.IsNull(put.Headers.ETag, "the stored data differs (SCHEDULE-STATUS), so no ETag may be returned");
        Assert.IsTrue(put.Headers.Contains("Schedule-Tag"));

        var orgCopy = (await Send(org, "GET", "/dav/calendars/organizer/default/meeting.ics")).Body.Replace("\r\n ", "");
        StringAssert.Contains(orgCopy, "SCHEDULE-STATUS=1.2");
        StringAssert.Contains(orgCopy, "SCHEDULE-STATUS=5.1"); // external attendee, email disabled

        // The attendee received the invitation in their default calendar and their inbox.
        var attUri = await WithDbAsync(async (db, _) => await db.CalendarObjects
            .Where(o => o.Calendar.Owner.Username == "attendee" && o.Calendar.Kind == CalendarKind.Normal && o.Uid == "meeting-1")
            .Select(o => o.Uri).SingleAsync());
        var inboxCount = await WithDbAsync(async (db, _) => await db.CalendarObjects
            .CountAsync(o => o.Calendar.Owner.Username == "attendee" && o.Calendar.Kind == CalendarKind.Inbox && o.Uid == "meeting-1"));
        Assert.AreEqual(1, inboxCount);

        // The attendee accepts: the organizer's copy is updated.
        var attCopy = (await Send(att, "GET", "/dav/calendars/attendee/default/" + attUri)).Body.Replace("\r\n ", "");
        var accepted = attCopy.Replace("PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:attendee@example.com", "PARTSTAT=ACCEPTED:mailto:attendee@example.com");
        Assert.AreNotEqual(attCopy, accepted);
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(att, "PUT", "/dav/calendars/attendee/default/" + attUri, accepted)).Status);
        orgCopy = (await Send(org, "GET", "/dav/calendars/organizer/default/meeting.ics")).Body.Replace("\r\n ", "");
        StringAssert.Contains(orgCopy.Replace("\r\n ", ""), "PARTSTAT=ACCEPTED;SCHEDULE-STATUS=2.0:mailto:attendee@example.com");

        // Free/busy lookup through the scheduling outbox.
        var fb = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            METHOD:REQUEST
            BEGIN:VFREEBUSY
            UID:fb-1
            DTSTAMP:20260101T000000Z
            DTSTART:20261110T000000Z
            DTEND:20261111T000000Z
            ORGANIZER:mailto:organizer@example.com
            ATTENDEE:mailto:attendee@example.com
            END:VFREEBUSY
            END:VCALENDAR
            """;
        var (fbStatus, fbBody) = await Send(org, "POST", "/dav/calendars/organizer/outbox/", fb);
        Assert.AreEqual(HttpStatusCode.OK, fbStatus);
        StringAssert.Contains(fbBody, "2.0;Success");
        StringAssert.Contains(fbBody, "FREEBUSY;FBTYPE=BUSY:20261110T100000Z/20261110T110000Z");

        // The organizer cancels by deleting the event.
        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(org, "DELETE", "/dav/calendars/organizer/default/meeting.ics")).Status);
        attCopy = (await Send(att, "GET", "/dav/calendars/attendee/default/" + attUri)).Body.Replace("\r\n ", "");
        StringAssert.Contains(attCopy, "STATUS:CANCELLED");

        // Principal advertises the scheduling collections.
        var (_, principal) = await Send(org, "PROPFIND", "/dav/principals/organizer/",
            """<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:schedule-inbox-URL/><c:schedule-outbox-URL/></d:prop></d:propfind>""",
            ("Depth", "0"));
        StringAssert.Contains(principal, "/dav/calendars/organizer/inbox/");
        StringAssert.Contains(principal, "/dav/calendars/organizer/outbox/");
    }

    // ------------------------------------------------------------------ Delegation and groups

    [TestMethod]
    public async Task DelegatesAndGroupsSeeCollections()
    {
        var boss = await CreateUserAsync("boss");
        var assistant = await CreateUserAsync("assistant");
        await WithDbAsync(async (db, sp) =>
        {
            db.Delegations.Add(new Delegation { OwnerId = boss.Id, DelegateId = assistant.Id, CanWrite = false });
            var group = new Group { Name = "staff", DisplayName = "Staff", Members = [new GroupMember { UserId = assistant.Id }] };
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            var cal = await sp.GetRequiredService<CalendarStore>().CreateAsync(boss.Id, "team-cal", "Team calendar");
            await sp.GetRequiredService<CalendarStore>().ShareWithGroupAsync(cal, group, ShareAccess.ReadWrite);
            return 0;
        });
        var c = await TestHost.ClientAsync("assistant", "assistant-password");

        // The delegate's principal is a member of the boss's proxy group, which points at the boss's calendar home.
        var (_, principal) = await Send(c, "PROPFIND", "/dav/principals/assistant/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:group-membership/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(principal, "/dav/principals/boss/calendar-proxy-read/");
        StringAssert.Contains(principal, "/dav/groups/staff/");
        var (_, proxy) = await Send(c, "PROPFIND", "/dav/principals/boss/calendar-proxy-read/",
            """<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-home-set/><d:group-member-set/></d:prop></d:propfind>""",
            ("Depth", "0"));
        StringAssert.Contains(proxy, "/dav/calendars/boss/");
        StringAssert.Contains(proxy, "/dav/principals/assistant/");

        var (status, home) = await Send(c, "PROPFIND", "/dav/calendars/boss/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:current-user-privilege-set/></d:prop></d:propfind>""", ("Depth", "1"));
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);
        StringAssert.Contains(home, "/dav/calendars/boss/default/");
        Assert.DoesNotContain("/dav/calendars/boss/inbox/", home);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(c, "PUT", "/dav/calendars/boss/default/x.ics", Event("delegate-write"))).Status);

        // The group share shows up in the member's own home with write access.
        var (_, own) = await Send(c, "PROPFIND", "/dav/calendars/assistant/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/></d:prop></d:propfind>""", ("Depth", "1"));
        StringAssert.Contains(own, "Team calendar");
        var groupUri = await WithDbAsync(async (db, _) => await db.CalendarShares.Where(s => s.GroupId != null && s.Calendar.Uri == "team-cal").Select(s => s.Uri).SingleAsync());
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", $"/dav/calendars/assistant/{groupUri}/g.ics", Event("group-write"))).Status);

        // Strangers still cannot read the home.
        var other = await TestHost.ClientAsync("bob", "bob-password");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(other, "PROPFIND", "/dav/calendars/boss/", null, ("Depth", "0"))).Status);
    }

    // ------------------------------------------------------------------ vCard version negotiation and generated collections

    [TestMethod]
    public async Task VCardVersionsAreNegotiatedAndBirthdaysGenerated()
    {
        await CreateUserAsync("cards");
        var c = await TestHost.ClientAsync("cards", "cards-password");
        const string v4 = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:v4-card\r\nFN:Ada Lovelace\r\nN:Lovelace;Ada;;;\r\nBDAY:--1210\r\n" +
                          "TEL;VALUE=uri;TYPE=cell:tel:+44-20-7946-0000\r\nPHOTO:data:image/png;base64,iVBORw0KGgo=\r\nEND:VCARD\r\n";
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/addressbooks/cards/default/ada.vcf", v4)).Status);

        var (_, v3) = await Send(c, "GET", "/dav/addressbooks/cards/default/ada.vcf");
        StringAssert.Contains(v3, "VERSION:3.0");
        StringAssert.Contains(v3, "PHOTO;ENCODING=b;TYPE=PNG:iVBORw0KGgo=");
        StringAssert.Contains(v3, "TEL;TYPE=cell:+44-20-7946-0000");
        StringAssert.Contains(v3, "BDAY;X-APPLE-OMIT-YEAR=1604:1604-12-10");

        var req = new HttpRequestMessage(HttpMethod.Get, "/dav/addressbooks/cards/default/ada.vcf");
        req.Headers.TryAddWithoutValidation("Accept", "text/vcard; version=4.0");
        StringAssert.Contains(await (await c.SendAsync(req)).Content.ReadAsStringAsync(), "VERSION:4.0");

        var (_, multiget) = await Send(c, "REPORT", "/dav/addressbooks/cards/default/",
            """<cr:addressbook-multiget xmlns:d="DAV:" xmlns:cr="urn:ietf:params:xml:ns:carddav"><d:prop><cr:address-data content-type="text/vcard" version="4.0"/></d:prop><d:href>/dav/addressbooks/cards/default/ada.vcf</d:href></cr:addressbook-multiget>""");
        StringAssert.Contains(multiget, "VERSION:4.0");

        // Birthday calendar (generated in the background).
        var birthday = await WaitForAsync(() => WithDbAsync(async (db, _) => await db.CalendarObjects
            .Where(o => o.Calendar.Owner.Username == "cards" && o.Calendar.Kind == CalendarKind.Birthdays).Select(o => o.Data).FirstOrDefaultAsync()));
        Assert.IsNotNull(birthday, "birthday event was not generated");
        StringAssert.Contains(birthday, "SUMMARY:Ada Lovelace's birthday");
        StringAssert.Contains(birthday, "DTSTART;VALUE=DATE:20001210");
        StringAssert.Contains(birthday, "RRULE:FREQ=YEARLY");

        // Directory address book (generated in the background) contains other users, read-only.
        var directoryHref = await WaitForAsync(() => WithDbAsync(async (db, _) =>
            await db.Cards.AnyAsync(x => x.AddressBook.Owner.Username == "cards" && x.AddressBook.Kind == AddressBookKind.Directory && x.Data.Contains("alice@example.com"))
                ? await db.AddressBooks.Where(b => b.Owner.Username == "cards" && b.Kind == AddressBookKind.Directory).Select(b => b.Uri).FirstAsync()
                : null));
        Assert.IsNotNull(directoryHref, "directory was not generated");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(c, "PUT", $"/dav/addressbooks/cards/{directoryHref}/x.vcf", v4)).Status);
    }

    // ------------------------------------------------------------------ Subscriptions, public links, push

    private sealed class FakeHttp : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public string Feed { get; set; } = "";
        public ConcurrentQueue<(string Path, Dictionary<string, string> Headers, byte[] Body)> Requests { get; } = new();

        public FakeHttp()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Loop();
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue((ctx.Request.Url!.AbsolutePath, headers, ms.ToArray()));
                if (ctx.Request.HttpMethod == "GET")
                {
                    var bytes = Encoding.UTF8.GetBytes(Feed);
                    ctx.Response.ContentType = "text/calendar";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                else ctx.Response.StatusCode = 201;
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    [TestMethod]
    public async Task SubscriptionsArePulledAndReadOnly()
    {
        using var http = new FakeHttp();
        http.Feed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//feed//EN\r\nBEGIN:VEVENT\r\nUID:holiday-1\r\nDTSTAMP:20260101T000000Z\r\n" +
                    "DTSTART;VALUE=DATE:20261225\r\nSUMMARY:Christmas\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:holiday-2\r\nDTSTAMP:20260101T000000Z\r\n" +
                    "DTSTART;VALUE=DATE:20261226\r\nSUMMARY:Boxing Day\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var u = await CreateUserAsync("subscriber");
        var calId = await WithDbAsync(async (db, sp) =>
        {
            var cal = new Calendar
            {
                OwnerId = u.Id, Uri = "holidays", DisplayName = "Holidays", Kind = CalendarKind.Subscription,
                SubscriptionUrl = $"webcal://localhost:{http.Port}/holidays.ics", Components = "VEVENT",
            };
            db.Calendars.Add(cal);
            await db.SaveChangesAsync();
            return cal.Id;
        });
        var error = await WithDbAsync((_, sp) => sp.GetRequiredService<SubscriptionService>().RefreshAsync(calId, default));
        Assert.IsNull(error, error);
        Assert.AreEqual(2, await WithDbAsync((db, _) => db.CalendarObjects.CountAsync(o => o.CalendarId == calId)));

        http.Feed = http.Feed.Replace("BEGIN:VEVENT\r\nUID:holiday-2\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:20261226\r\nSUMMARY:Boxing Day\r\nEND:VEVENT\r\n", "");
        await WithDbAsync((_, sp) => sp.GetRequiredService<SubscriptionService>().RefreshAsync(calId, default));
        Assert.AreEqual(1, await WithDbAsync((db, _) => db.CalendarObjects.CountAsync(o => o.CalendarId == calId)));

        var c = await TestHost.ClientAsync("subscriber", "subscriber-password");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(c, "PUT", "/dav/calendars/subscriber/holidays/x.ics", Event("nope"))).Status);

        Assert.IsFalse(OutboundHttp.IsPublic(IPAddress.Parse("127.0.0.1")));
        Assert.IsFalse(OutboundHttp.IsPublic(IPAddress.Parse("10.1.2.3")));
        Assert.IsFalse(OutboundHttp.IsPublic(IPAddress.Parse("169.254.169.254")));
        Assert.IsFalse(OutboundHttp.IsPublic(IPAddress.Parse("::ffff:192.168.1.1")));
        Assert.IsTrue(OutboundHttp.IsPublic(IPAddress.Parse("1.1.1.1")));
    }

    [TestMethod]
    public async Task PublicLinksServeBusyOnlyFeeds()
    {
        var u = await CreateUserAsync("publisher");
        var c = await TestHost.ClientAsync("publisher", "publisher-password");
        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/calendars/publisher/default/secret.ics",
            Event("secret-1", "DESCRIPTION:Top secret"))).Status);
        const string token = "abcdefghijklmnopqrstuvwxyz012345";
        await WithDbAsync(async (db, _) => await db.Calendars.Where(x => x.OwnerId == u.Id && x.Uri == "default")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublicToken, token).SetProperty(x => x.PublicBusyOnly, true)));

        var anon = await TestHost.ClientAsync();
        var feed = await anon.GetStringAsync($"/public/calendar/{token}.ics");
        StringAssert.Contains(feed, "SUMMARY:Busy");
        Assert.DoesNotContain("Top secret", feed);
        Assert.AreEqual(HttpStatusCode.NotFound, (await anon.GetAsync("/public/calendar/wrong-token-wrong-token-wrong.ics")).StatusCode);
    }

    [TestMethod]
    public async Task WebDavPushDeliversEncryptedNotifications()
    {
        using var http = new FakeHttp();
        await CreateUserAsync("pusher");
        var c = await TestHost.ClientAsync("pusher", "pusher-password");

        var (_, props) = await Send(c, "PROPFIND", "/dav/calendars/pusher/default/",
            """<d:propfind xmlns:d="DAV:" xmlns:p="https://bitfire.at/webdav-push"><d:prop><p:transports/><p:topic/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(props, "vapid-public-key");
        var topic = System.Text.RegularExpressions.Regex.Match(props, "<push:topic>([^<]+)").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(topic));

        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ua.ExportParameters(false);
        byte[] uaPublic = [0x04, .. p.Q.X!, .. p.Q.Y!];
        var auth = RandomNumberGenerator.GetBytes(16);
        var register = $"""
            <P:push-register xmlns:P="https://bitfire.at/webdav-push" xmlns:D="DAV:">
              <P:subscription><P:web-push-subscription>
                <P:push-resource>http://localhost:{http.Port}/push/abc</P:push-resource>
                <P:content-encoding>aes128gcm</P:content-encoding>
                <P:subscription-public-key type="p256dh">{PushService.Base64Url(uaPublic)}</P:subscription-public-key>
                <P:auth-secret>{PushService.Base64Url(auth)}</P:auth-secret>
              </P:web-push-subscription></P:subscription>
              <P:trigger><P:content-update><D:depth>1</D:depth></P:content-update></P:trigger>
            </P:push-register>
            """;
        var resp = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/dav/calendars/pusher/default/")
        {
            Content = new StringContent(register, Encoding.UTF8, "application/xml"),
        });
        Assert.AreEqual(HttpStatusCode.Created, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        StringAssert.StartsWith(location, "/dav/push/");

        Assert.AreEqual(HttpStatusCode.Created, (await Send(c, "PUT", "/dav/calendars/pusher/default/p.ics", Event("push-1"))).Status);

        var request = await WaitForAsync(() => Task.FromResult(http.Requests.TryDequeue(out var r) ? new[] { r } : null));
        Assert.IsNotNull(request, "no push message was delivered");
        var (path, headers, body) = request[0];
        Assert.AreEqual("/push/abc", path);
        Assert.AreEqual("aes128gcm", headers["Content-Encoding"]);
        StringAssert.StartsWith(headers["Authorization"], "vapid t=");

        var message = Encoding.UTF8.GetString(Decrypt(body, ua, uaPublic, auth));
        StringAssert.Contains(message, "push-message");
        StringAssert.Contains(message, topic);
        StringAssert.Contains(message, "sync-token");

        Assert.AreEqual(HttpStatusCode.NoContent, (await Send(c, "DELETE", location)).Status);
    }

    /// <summary>RFC 8291 decryption (user agent side) to verify the server's encryption.</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman ua, byte[] uaPublic, byte[] auth)
    {
        var salt = body[..16];
        var idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var ciphertext = body[(21 + idLen)..];
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var secret = ua.DeriveRawSecretAgreement(sender.PublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, secret, auth);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, [.. "WebPush: info"u8, 0, .. uaPublic, .. asPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, [.. "Content-Encoding: aes128gcm"u8, 0]);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, [.. "Content-Encoding: nonce"u8, 0]);
        var plain = new byte[ciphertext.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        return plain[..end];
    }
}
