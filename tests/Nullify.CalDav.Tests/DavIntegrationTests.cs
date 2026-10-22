using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class DavIntegrationTests
{
    private static WebApplicationFactory<Program> _factory = null!;

    [ClassInitialize]
    public static async Task Init(TestContext _) => _factory = await TestHost.GetAsync();

    private static HttpClient Client(string user, string password)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return c;
    }
    internal static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient c, string method, string url, string? body = null,
        params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, body.StartsWith('<') ? "application/xml" : "text/calendar");
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        var resp = await c.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static string Event(string uid) => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20260101T000000Z
        DTSTART:20260601T100000Z
        DTEND:20260601T110000Z
        SUMMARY:Test {uid}
        END:VEVENT
        END:VCALENDAR
        """;

    [TestMethod]
    public async Task RejectsBadCredentialsAndOtherUsersHomes()
    {
        var (status, _) = await SendAsync(Client("alice", "wrong"), "PROPFIND", "/dav/");
        Assert.AreEqual(HttpStatusCode.Unauthorized, status);

        (status, _) = await SendAsync(Client("bob", "bob-password"), "PROPFIND", "/dav/calendars/alice/default/", null, ("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.Forbidden, status);
    }

    [TestMethod]
    public async Task AppPasswordsAuthenticate()
    {
        var token = PasswordService.GenerateToken();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bob = await db.Users.FirstAsync(u => u.Username == "bob");
            db.AppPasswords.Add(new AppPassword { UserId = bob.Id, Name = "test", TokenHash = PasswordService.Sha256(token) });
            await db.SaveChangesAsync();
        }
        var (status, _) = await SendAsync(Client("bob", token), "PROPFIND", "/dav/", null, ("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);
    }

    [TestMethod]
    public async Task SharingHonoursAccessLevel()
    {
        var alice = Client("alice", "alice-password");
        var bob = Client("bob", "bob-password");

        Assert.AreEqual(HttpStatusCode.Created, (await SendAsync(alice, "MKCALENDAR", "/dav/calendars/alice/team/")).Status);
        Assert.AreEqual(HttpStatusCode.Created, (await SendAsync(alice, "PUT", "/dav/calendars/alice/team/a.ics", Event("share-a"))).Status);

        string shareUri;
        int calId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cal = await db.Calendars.FirstAsync(c => c.Uri == "team" && c.Owner.Username == "alice");
            var bobUser = await db.Users.FirstAsync(u => u.Username == "bob");
            var share = await scope.ServiceProvider.GetRequiredService<CalendarStore>().ShareAsync(cal, bobUser, ShareAccess.Read);
            shareUri = share.Uri;
            calId = cal.Id;
        }

        // Shared calendar shows up in Bob's home with read-only privileges.
        var (status, body) = await SendAsync(bob, "PROPFIND", "/dav/calendars/bob/", """
            <d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:current-user-privilege-set/><d:owner/></d:prop></d:propfind>
            """, ("Depth", "1"));
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);
        StringAssert.Contains(body, $"/dav/calendars/bob/{shareUri}/");
        StringAssert.Contains(body, "/dav/principals/alice/");

        (status, body) = await SendAsync(bob, "GET", $"/dav/calendars/bob/{shareUri}/a.ics");
        Assert.AreEqual(HttpStatusCode.OK, status);
        StringAssert.Contains(body, "share-a");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await SendAsync(bob, "PUT", $"/dav/calendars/bob/{shareUri}/b.ics", Event("share-b"))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await SendAsync(bob, "DELETE", $"/dav/calendars/bob/{shareUri}/a.ics")).Status);

        // Upgrade to read-write.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.CalendarShares.Where(s => s.CalendarId == calId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Access, ShareAccess.ReadWrite));
        }
        Assert.AreEqual(HttpStatusCode.Created, (await SendAsync(bob, "PUT", $"/dav/calendars/bob/{shareUri}/b.ics", Event("share-b"))).Status);
        (status, body) = await SendAsync(alice, "GET", "/dav/calendars/alice/team/b.ics");
        Assert.AreEqual(HttpStatusCode.OK, status);

        // Sharee can rename their own view without affecting the owner.
        (status, _) = await SendAsync(bob, "PROPPATCH", $"/dav/calendars/bob/{shareUri}/", """
            <d:propertyupdate xmlns:d="DAV:"><d:set><d:prop><d:displayname>Alice's team</d:displayname></d:prop></d:set></d:propertyupdate>
            """);
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);
        (_, body) = await SendAsync(alice, "PROPFIND", "/dav/calendars/alice/team/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/></d:prop></d:propfind>""", ("Depth", "0"));
        Assert.DoesNotContain("Alice's team", body);

        // Deleting the share from the sharee's side leaves the calendar intact.
        Assert.AreEqual(HttpStatusCode.NoContent, (await SendAsync(bob, "DELETE", $"/dav/calendars/bob/{shareUri}/")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await SendAsync(bob, "GET", $"/dav/calendars/bob/{shareUri}/a.ics")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await SendAsync(alice, "GET", "/dav/calendars/alice/team/a.ics")).Status);
    }

    [TestMethod]
    public async Task SyncCollectionTracksChanges()
    {
        var alice = Client("alice", "alice-password");
        Assert.AreEqual(HttpStatusCode.Created, (await SendAsync(alice, "MKCALENDAR", "/dav/calendars/alice/sync/")).Status);
        const string syncInitial = """<d:sync-collection xmlns:d="DAV:"><d:sync-token/><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>""";
        var (_, body) = await SendAsync(alice, "REPORT", "/dav/calendars/alice/sync/", syncInitial);
        var token = System.Text.RegularExpressions.Regex.Match(body, "<d:sync-token>([^<]+)").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(token));

        await SendAsync(alice, "PUT", "/dav/calendars/alice/sync/1.ics", Event("sync-1"));
        await SendAsync(alice, "PUT", "/dav/calendars/alice/sync/2.ics", Event("sync-2"));
        await SendAsync(alice, "DELETE", "/dav/calendars/alice/sync/1.ics");

        (_, body) = await SendAsync(alice, "REPORT", "/dav/calendars/alice/sync/",
            $"""<d:sync-collection xmlns:d="DAV:"><d:sync-token>{token}</d:sync-token><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>""");
        StringAssert.Contains(body, "/dav/calendars/alice/sync/1.ics</d:href><d:status>HTTP/1.1 404 Not Found");
        StringAssert.Contains(body, "/dav/calendars/alice/sync/2.ics</d:href><d:propstat>");

        // Limit truncation returns a 507 for the collection and a resumable token.
        (_, body) = await SendAsync(alice, "REPORT", "/dav/calendars/alice/sync/",
            $"""<d:sync-collection xmlns:d="DAV:"><d:sync-token>{token}</d:sync-token><d:sync-level>1</d:sync-level><d:limit><d:nresults>1</d:nresults></d:limit><d:prop><d:getetag/></d:prop></d:sync-collection>""");
        StringAssert.Contains(body, "507");
    }

    [TestMethod]
    public async Task UiRequiresLoginAndAdminPagesRequireAdmin()
    {
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await anon.GetAsync("/calendars");
        Assert.AreEqual(HttpStatusCode.Redirect, resp.StatusCode);
        StringAssert.Contains(resp.Headers.Location!.ToString(), "/account/login");
        Assert.AreEqual(HttpStatusCode.OK, (await anon.GetAsync("/account/login")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await anon.GetAsync("/health")).StatusCode);

        foreach (var wellKnown in new[] { "/.well-known/caldav", "/.well-known/carddav/" })
        {
            var r = await anon.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), wellKnown));
            Assert.AreEqual(HttpStatusCode.MovedPermanently, r.StatusCode);
            Assert.AreEqual("/dav/", r.Headers.Location!.ToString());
        }
    }
}
