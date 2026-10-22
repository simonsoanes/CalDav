using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class MigrationTests
{
    /// <summary>Minimal Baïkal-like CalDAV/CardDAV server with Digest authentication.</summary>
    private sealed class FakeRemoteServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public string Base => $"http://localhost:{Port}";
        public Dictionary<string, string> Calendar { get; } = new();
        public Dictionary<string, string> Contacts { get; } = new();
        public int Unauthorized;

        public FakeRemoteServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Loop();
        }

        private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        private bool Authorized(HttpListenerRequest req)
        {
            var h = req.Headers["Authorization"];
            if (h == null || !h.StartsWith("Digest ")) return false;
            var p = Regex.Matches(h[7..], "(\\w+)=(?:\"([^\"]*)\"|([^,\\s]*))").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Value);
            var ha1 = Md5("olduser:BaikalDAV:old-secret");
            var ha2 = Md5($"{req.HttpMethod}:{p["uri"]}");
            var expected = Md5($"{ha1}:{p["nonce"]}:{p["nc"]}:{p["cnonce"]}:{p["qop"]}:{ha2}");
            return p.GetValueOrDefault("username") == "olduser" && p["response"] == expected;
        }

        private static string Ms(params string[] responses) =>
            "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\" xmlns:cr=\"urn:ietf:params:xml:ns:carddav\" xmlns:a=\"http://apple.com/ns/ical/\">" +
            string.Concat(responses) + "</d:multistatus>";

        private static string R(string href, string props) =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>{props}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                try { await Handle(ctx); }
                catch { ctx.Response.StatusCode = 500; }
                ctx.Response.Close();
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var path = req.Url!.AbsolutePath;
            var body = await new StreamReader(req.InputStream).ReadToEndAsync();

            if (!Authorized(req))
            {
                Interlocked.Increment(ref Unauthorized);
                res.StatusCode = 401;
                res.AddHeader("WWW-Authenticate", "Digest realm=\"BaikalDAV\", nonce=\"" + Guid.NewGuid().ToString("N") + "\", qop=\"auth\", algorithm=MD5");
                return;
            }

            string? xml = null;
            int status = 207;
            switch (req.HttpMethod, path)
            {
                case ("PROPFIND", "/"):
                    res.StatusCode = 301;
                    res.RedirectLocation = "/dav/";
                    return;
                case ("PROPFIND", "/dav/"):
                    xml = Ms(R("/dav/", "<d:current-user-principal><d:href>/dav/principals/olduser/</d:href></d:current-user-principal>"));
                    break;
                case ("PROPFIND", "/dav/principals/olduser/"):
                    xml = Ms(R(path, "<c:calendar-home-set><d:href>/dav/cal/olduser/</d:href></c:calendar-home-set>" +
                                     "<cr:addressbook-home-set><d:href>/dav/card/olduser/</d:href></cr:addressbook-home-set>"));
                    break;
                case ("PROPFIND", "/dav/cal/olduser/") when req.Headers["Depth"] == "1":
                    xml = Ms(
                        R(path, "<d:resourcetype><d:collection/></d:resourcetype>"),
                        R("/dav/cal/olduser/work/", "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>Work</d:displayname>" +
                                                    "<a:calendar-color>#FF0000FF</a:calendar-color><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set>"),
                        R("/dav/cal/olduser/inbox/", "<d:resourcetype><d:collection/><c:schedule-inbox/></d:resourcetype>"));
                    break;
                case ("PROPFIND", "/dav/card/olduser/") when req.Headers["Depth"] == "1":
                    xml = Ms(
                        R(path, "<d:resourcetype><d:collection/></d:resourcetype>"),
                        R("/dav/card/olduser/contacts/", "<d:resourcetype><d:collection/><cr:addressbook/></d:resourcetype><d:displayname>Old contacts</d:displayname>"));
                    break;
                case ("PROPFIND", "/dav/cal/olduser/work/"):
                    xml = Ms([R(path, "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype>"),
                        .. Calendar.Keys.Select(k => R(path + k, $"<d:getetag>\"{k.GetHashCode()}\"</d:getetag><d:resourcetype/>"))]);
                    break;
                case ("PROPFIND", "/dav/card/olduser/contacts/"):
                    xml = Ms([R(path, "<d:resourcetype><d:collection/><cr:addressbook/></d:resourcetype>"),
                        .. Contacts.Keys.Select(k => R(path + k, $"<d:getetag>\"{k.GetHashCode()}\"</d:getetag><d:resourcetype/>"))]);
                    break;
                case ("REPORT", "/dav/cal/olduser/work/"):
                {
                    var hrefs = XDocument.Parse(body).Descendants(XName.Get("href", "DAV:")).Select(h => h.Value).ToList();
                    xml = Ms(hrefs.Select(h => R(h, $"<d:getetag>\"x\"</d:getetag><c:calendar-data>{System.Security.SecurityElement.Escape(Calendar[h.Split('/').Last()])}</c:calendar-data>")).ToArray());
                    break;
                }
                case ("REPORT", _):
                    status = 400; // force the GET fallback for address books
                    break;
                case ("GET", _) when path.StartsWith("/dav/card/olduser/contacts/"):
                {
                    var bytes = Encoding.UTF8.GetBytes(Contacts[path.Split('/').Last()]);
                    res.ContentType = "text/vcard";
                    await res.OutputStream.WriteAsync(bytes);
                    return;
                }
                default:
                    status = 404;
                    break;
            }
            res.StatusCode = status;
            if (xml != null)
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                res.ContentType = "application/xml; charset=utf-8";
                await res.OutputStream.WriteAsync(bytes);
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static string Ev(string uid, string summary, string comp = "VEVENT") => $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Baikal//EN\r\nBEGIN:{comp}\r\nUID:{uid}\r\n" +
        $"DTSTAMP:20260101T000000Z\r\nDTSTART:20260301T090000Z\r\n{(comp == "VEVENT" ? "DTEND:20260301T100000Z\r\n" : "")}SUMMARY:{summary}\r\nEND:{comp}\r\nEND:VCALENDAR\r\n";

    [TestMethod]
    public async Task ImportsCalendarsAndContactsFromAnotherServer()
    {
        using var remote = new FakeRemoteServer();
        remote.Calendar["a.ics"] = Ev("old-1", "Dentist");
        remote.Calendar["b.ics"] = Ev("old-2", "Board meeting");
        remote.Calendar["task.ics"] = Ev("old-3", "File taxes", "VTODO");
        remote.Contacts["jane.vcf"] = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:c-1\r\nFN:Jane Old\r\nN:Old;Jane;;;\r\nEND:VCARD\r\n";
        remote.Contacts["legacy.vcf"] = "BEGIN:VCARD\r\nVERSION:2.1\r\nN:Legacy;Larry;;;\r\nTEL;CELL:+1 555 0100\r\nEND:VCARD\r\n";

        var factory = await TestHost.GetAsync();
        int userId, sourceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await scope.ServiceProvider.GetRequiredService<UserService>().CreateAsync("migrator", "Migrator", "migrator@example.com", "migrator-password", false);
            userId = user.Id;
            var migrations = scope.ServiceProvider.GetRequiredService<MigrationService>();

            await Assert.ThrowsExactlyAsync<RemoteDavException>(() =>
                migrations.ConnectAsync(db, user, remote.Base + "/", "olduser", "wrong", null, default));

            sourceId = await migrations.ConnectAsync(db, user, remote.Base + "/", "olduser", "old-secret", null, default);
            var found = await db.MigrationCollections.Where(c => c.SourceId == sourceId).ToListAsync();
            Assert.HasCount(2, found, "the scheduling inbox must not be offered");
            Assert.IsTrue(await migrations.EnqueueAsync(db, sourceId, userId, default));
        }

        async Task<MigrationSource> WaitAsync()
        {
            for (var i = 0; i < 100; i++)
            {
                using var scope = factory.Services.CreateScope();
                var s = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MigrationSources.AsNoTracking().FirstAsync(x => x.Id == sourceId);
                if (s.Status is MigrationStatus.Completed or MigrationStatus.Failed) return s;
                await Task.Delay(100);
            }
            Assert.Fail("Import did not finish");
            return null!;
        }

        var result = await WaitAsync();
        Assert.AreEqual(MigrationStatus.Completed, result.Status, result.Message);
        Assert.AreEqual(5, result.ItemsDone, result.Message);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cal = await db.Calendars.SingleAsync(c => c.OwnerId == userId && c.DisplayName == "Work");
            Assert.AreEqual("#FF0000FF", cal.Color);
            Assert.AreEqual(3, await db.CalendarObjects.CountAsync(o => o.CalendarId == cal.Id));
            Assert.AreEqual(1, await db.CalendarObjects.CountAsync(o => o.CalendarId == cal.Id && o.ComponentType == "VTODO"));
            var book = await db.AddressBooks.SingleAsync(b => b.OwnerId == userId && b.DisplayName == "Old contacts");
            var cards = await db.Cards.Where(c => c.AddressBookId == book.Id).ToListAsync();
            Assert.HasCount(2, cards);
            StringAssert.Contains(cards.Single(c => c.Uri == "legacy.vcf").Data, "VERSION:3.0");
        }

        // A second run picks up changes without duplicating anything.
        remote.Calendar["a.ics"] = Ev("old-1", "Dentist (moved)");
        remote.Calendar["c.ics"] = Ev("old-4", "New on old server");
        using (var scope = factory.Services.CreateScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<MigrationService>()
                .EnqueueAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), sourceId, userId, default));
        result = await WaitAsync();
        Assert.AreEqual(MigrationStatus.Completed, result.Status, result.Message);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cal = await db.Calendars.SingleAsync(c => c.OwnerId == userId && c.DisplayName == "Work");
            var objects = await db.CalendarObjects.Where(o => o.CalendarId == cal.Id).ToListAsync();
            Assert.HasCount(4, objects);
            StringAssert.Contains(objects.Single(o => o.Uid == "old-1").Data, "Dentist (moved)");
        }

        // The imported data is served over CalDAV like any other calendar.
        var client = await TestHost.ClientAsync("migrator", "migrator-password");
        var (status, _) = await DavIntegrationTests.SendAsync(client, "GET", "/dav/calendars/migrator/work/a.ics");
        Assert.AreEqual(HttpStatusCode.OK, status);
    }
}
