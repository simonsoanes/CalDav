using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.SyncMl;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class SyncMlTests
{
    private static XElement E(string name, params object?[] content) => new(name, content);

    private static XElement Message(string session, int msgId, string device, bool withCred, params XElement[] commands)
    {
        var hdr = E("SyncHdr", E("VerDTD", "1.2"), E("VerProto", "SyncML/1.2"), E("SessionID", session), E("MsgID", msgId.ToString()),
            E("Target", E("LocURI", "http://localhost/syncml")), E("Source", E("LocURI", device)),
            withCred ? E("Cred", E("Meta", E("Type", "syncml:auth-basic"), E("Format", "b64")),
                E("Data", Convert.ToBase64String(Encoding.UTF8.GetBytes("syncer:syncer-password")))) : null,
            E("Meta", E("MaxMsgSize", "20000")));
        return E("SyncML", hdr, E("SyncBody", commands, E("Final")));
    }

    private static async Task<XElement> PostAsync(XElement message, bool wbxml = false)
    {
        var client = await TestHost.ClientAsync();
        HttpContent content;
        if (wbxml)
        {
            content = new ByteArrayContent(Wbxml.Encode(message, Wbxml.Language.SyncMl, Wbxml.PublicIdSyncMl12));
            content.Headers.ContentType = new MediaTypeHeaderValue(SyncMlEndpoint.WbxmlType);
        }
        else
        {
            content = new ByteArrayContent(SyncMlEndpoint.SerializeXml(message, "1.2"));
            content.Headers.ContentType = new MediaTypeHeaderValue(SyncMlEndpoint.XmlType);
        }
        var resp = await client.PostAsync("/syncml", content);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return wbxml
            ? Wbxml.Decode(bytes, out _)
            : SyncMlXml.StripNamespaces(XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!);
    }

    private static IEnumerable<XElement> Statuses(XElement msg, string cmd) =>
        msg.Element("SyncBody")!.Elements("Status").Where(s => s.Element("Cmd")?.Value == cmd);

    private const string PhoneCard = "BEGIN:VCARD\r\nVERSION:2.1\r\nN:Person;Phone;;;\r\nTEL;CELL:+44 7700 900000\r\nEND:VCARD\r\n";

    [TestMethod]
    public async Task ContactsTwoWaySyncWithSlowSyncThenIncremental()
    {
        var alice = await TestHost.ClientAsync("syncer", "syncer-password");
        var put = await DavIntegrationTests.SendAsync(alice, "PUT", "/dav/addressbooks/syncer/default/server-1.vcf",
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:server-1\r\nFN:Server Person\r\nN:Person;Server;;;\r\nEND:VCARD\r\n");
        Assert.AreEqual(HttpStatusCode.Created, put.Status);

        const string device = "IMEI:350000000000001";
        var devinf = E("DevInf", E("VerDTD", "1.2"), E("Man", "Nokia"), E("Mod", "E71"), E("DevID", device), E("DevTyp", "phone"),
            E("DataStore", E("SourceRef", "./contacts"), E("Rx-Pref", E("CTType", "text/x-vcard"), E("VerCT", "2.1"))));

        // Package 1: authenticate, alert two-way sync, send device info.
        var r1 = await PostAsync(Message("1", 1, device, true,
            E("Alert", E("CmdID", "1"), E("Data", "200"),
                E("Item", E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./contacts")),
                    E("Meta", E("Anchor", E("Last", "0"), E("Next", "100"))))),
            E("Put", E("CmdID", "2"), E("Meta", E("Type", "application/vnd.syncml-devinf+xml")),
                E("Item", E("Source", E("LocURI", "./devinf12")), E("Data", devinf)))));
        Assert.AreEqual("212", Statuses(r1, "SyncHdr").Single().Element("Data")!.Value);
        Assert.AreEqual("508", Statuses(r1, "Alert").Single().Element("Data")!.Value, "unknown anchors require a slow sync");
        Assert.AreEqual("201", r1.Element("SyncBody")!.Element("Alert")!.Element("Data")!.Value);

        // Package 3: the client sends all its items.
        var r2 = await PostAsync(Message("1", 2, device, false,
            E("Sync", E("CmdID", "1"), E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./contacts")),
                E("Add", E("CmdID", "2"), E("Meta", E("Type", "text/x-vcard")),
                    E("Item", E("Source", E("LocURI", "1")), E("Data", PhoneCard))))));
        Assert.AreEqual("201", Statuses(r2, "Add").Single().Element("Data")!.Value);
        var serverSync = r2.Element("SyncBody")!.Element("Sync")!;
        var add = serverSync.Elements("Add").Single();
        Assert.AreEqual("server-1.vcf", add.Element("Item")!.Element("Source")!.Element("LocURI")!.Value);
        StringAssert.Contains(add.Element("Item")!.Element("Data")!.Value, "VERSION:2.1");
        StringAssert.Contains(add.Element("Item")!.Element("Data")!.Value, "Server Person");
        Assert.IsNotNull(r2.Element("SyncBody")!.Element("Final"));

        // Package 5: map the server item to the client's local id.
        var r3 = await PostAsync(Message("1", 3, device, false,
            E("Map", E("CmdID", "1"), E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./contacts")),
                E("MapItem", E("Target", E("LocURI", "server-1.vcf")), E("Source", E("LocURI", "2"))))));
        Assert.AreEqual("200", Statuses(r3, "Map").Single().Element("Data")!.Value);

        string phoneUri;
        using (var scope = (await TestHost.GetAsync()).Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cards = await db.Cards.Where(c => c.AddressBook.Owner.Username == "syncer" && c.AddressBook.Kind == AddressBookKind.Normal).ToListAsync();
            Assert.HasCount(2, cards);
            var phone = cards.Single(c => c.Uri != "server-1.vcf");
            StringAssert.Contains(phone.Data, "VERSION:3.0");
            StringAssert.Contains(phone.Data, "FN:Phone Person");
            phoneUri = phone.Uri;
        }

        // Change on the server between sessions.
        put = await DavIntegrationTests.SendAsync(alice, "PUT", "/dav/addressbooks/syncer/default/server-1.vcf",
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:server-1\r\nFN:Server Person Updated\r\nN:Person;Server;;;\r\nEND:VCARD\r\n");
        Assert.AreEqual(HttpStatusCode.NoContent, put.Status);

        // Second session: anchors match, so a normal two-way sync happens.
        var s1 = await PostAsync(Message("2", 1, device, true,
            E("Alert", E("CmdID", "1"), E("Data", "200"),
                E("Item", E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./contacts")),
                    E("Meta", E("Anchor", E("Last", "100"), E("Next", "200")))))));
        Assert.AreEqual("200", Statuses(s1, "Alert").Single().Element("Data")!.Value);
        Assert.AreEqual("200", s1.Element("SyncBody")!.Element("Alert")!.Element("Data")!.Value);

        var s2 = await PostAsync(Message("2", 2, device, false,
            E("Sync", E("CmdID", "1"), E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./contacts")),
                E("Delete", E("CmdID", "2"), E("Item", E("Source", E("LocURI", "1")))))));
        Assert.AreEqual("200", Statuses(s2, "Delete").Single().Element("Data")!.Value);
        var replace = s2.Element("SyncBody")!.Element("Sync")!.Elements("Replace").Single();
        Assert.AreEqual("2", replace.Element("Item")!.Element("Target")!.Element("LocURI")!.Value);
        StringAssert.Contains(replace.Element("Item")!.Element("Data")!.Value, "Server Person Updated");

        var (status, _) = await DavIntegrationTests.SendAsync(alice, "GET", "/dav/addressbooks/syncer/default/" + phoneUri);
        Assert.AreEqual(HttpStatusCode.NotFound, status, "the client's delete must be applied on the server");
    }

    [TestMethod]
    public async Task CalendarSyncOverWbxml()
    {
        const string device = "IMEI:350000000000002";
        var r1 = await PostAsync(Message("10", 1, device, true,
            E("Alert", E("CmdID", "1"), E("Data", "201"),
                E("Item", E("Target", E("LocURI", "calendar")), E("Source", E("LocURI", "./C\\Calendar")),
                    E("Meta", E("Anchor", E("Next", "1")))))), wbxml: true);
        Assert.AreEqual("212", Statuses(r1, "SyncHdr").Single().Element("Data")!.Value);
        Assert.AreEqual("201", r1.Element("SyncBody")!.Element("Alert")!.Element("Data")!.Value);
        // No device info is known yet, so the server asks for it.
        Assert.IsNotNull(r1.Element("SyncBody")!.Element("Get"));

        const string vcal = "BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nSUMMARY:Dentist\r\nDTSTART:20261103T140000Z\r\nDTEND:20261103T150000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var r2 = await PostAsync(Message("10", 2, device, false,
            E("Results", E("CmdID", "1"), E("MsgRef", "1"), E("CmdRef", "4"),
                E("Item", E("Source", E("LocURI", "./devinf12")), E("Data",
                    E("DevInf", E("VerDTD", "1.2"), E("Man", "SonyEricsson"), E("Mod", "K800i"), E("DevID", device), E("DevTyp", "phone"),
                        E("DataStore", E("SourceRef", "./C\\Calendar"), E("Rx-Pref", E("CTType", "text/x-vcalendar"), E("VerCT", "1.0"))))))),
            E("Sync", E("CmdID", "2"), E("Target", E("LocURI", "calendar")), E("Source", E("LocURI", "./C\\Calendar")),
                E("Add", E("CmdID", "3"), E("Meta", E("Type", "text/x-vcalendar")),
                    E("Item", E("Source", E("LocURI", "77")), E("Data", vcal))))), wbxml: true);
        Assert.AreEqual("201", Statuses(r2, "Add").Single().Element("Data")!.Value);
        Assert.IsNotNull(r2.Element("SyncBody")!.Element("Sync"));

        using var scope = (await TestHost.GetAsync()).Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var obj = await db.CalendarObjects.SingleAsync(o => o.Calendar.Owner.Username == "syncer" && o.Data.Contains("Dentist"));
        StringAssert.Contains(obj.Data, "VERSION:2.0");
        Assert.AreEqual("VEVENT", obj.ComponentType);
        var device2 = await db.SyncMlDevices.SingleAsync(d => d.DeviceId == device);
        Assert.AreEqual("K800i", device2.Model);
    }

    [TestMethod]
    public async Task RejectsBadCredentialsWithChallenge()
    {
        var msg = Message("99", 1, "IMEI:bad", false,
            E("Alert", E("CmdID", "1"), E("Data", "200"), E("Item", E("Target", E("LocURI", "contacts")), E("Source", E("LocURI", "./c")))));
        msg.Element("SyncHdr")!.Element("Meta")!.AddBeforeSelf(E("Cred", E("Meta", E("Type", "syncml:auth-basic")),
            E("Data", Convert.ToBase64String(Encoding.UTF8.GetBytes("syncer:wrong")))));
        var r = await PostAsync(msg);
        var status = Statuses(r, "SyncHdr").Single();
        Assert.AreEqual("401", status.Element("Data")!.Value);
        Assert.IsNotNull(status.Element("Chal"));
    }

    [TestMethod]
    public void WbxmlRoundTrip()
    {
        var msg = Message("5", 1, "dev", true,
            E("Alert", E("CmdID", "1"), E("Data", "200"),
                E("Item", E("Target", E("LocURI", "contacts")), E("Meta", E("Anchor", E("Last", "1"), E("Next", "2"))))),
            E("Put", E("CmdID", "2"), E("Item", E("Data", E("DevInf", E("VerDTD", "1.2"), E("Man", "X"))))));
        var bytes = Wbxml.Encode(msg, Wbxml.Language.SyncMl, Wbxml.PublicIdSyncMl12);
        var decoded = Wbxml.Decode(bytes, out var publicId);
        Assert.AreEqual(Wbxml.PublicIdSyncMl12, publicId);
        Assert.AreEqual(msg.ToString(SaveOptions.DisableFormatting), decoded.ToString(SaveOptions.DisableFormatting));
    }
}
