using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class ImapAndAutoconfigTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImapAuthenticatorUsesAuthPlainOrLogin(bool authPlain)
    {
        using var server = new FakeImapServer { OfferAuthPlain = authPlain };
        var imap = new ImapAuthenticator(NullLogger<ImapAuthenticator>.Instance);
        var ep = new ImapEndpoint("127.0.0.1", server.Port, MailSecurity.None, false);

        Assert.AreEqual(ImapResult.Success, await imap.AuthenticateAsync(ep, "carol@example.com", "imap-secret", default));
        Assert.AreEqual(ImapResult.InvalidCredentials, await imap.AuthenticateAsync(ep, "carol@example.com", "wrong", default));
        Assert.AreEqual(2, server.Logins);
    }

    [TestMethod]
    public async Task ImapAuthenticatorReportsUnavailableServer()
    {
        var imap = new ImapAuthenticator(NullLogger<ImapAuthenticator>.Instance);
        var ep = new ImapEndpoint("127.0.0.1", 1, MailSecurity.None, false);
        Assert.AreEqual(ImapResult.Unavailable, await imap.AuthenticateAsync(ep, "a", "b", default));
    }

    [TestMethod]
    public async Task ImapPassthroughAuthenticatesDavAndUi()
    {
        // carol uses IMAP passthrough; email login is accepted too.
        var ok = await TestHost.ClientAsync("carol", "imap-secret");
        var (status, _) = await DavIntegrationTests.SendAsync(ok, "PROPFIND", "/dav/", null, ("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);

        var byEmail = await TestHost.ClientAsync("carol@example.com", "imap-secret");
        (status, _) = await DavIntegrationTests.SendAsync(byEmail, "PROPFIND", "/dav/", null, ("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.MultiStatus, status);

        var bad = await TestHost.ClientAsync("carol", "not-it");
        (status, _) = await DavIntegrationTests.SendAsync(bad, "PROPFIND", "/dav/", null, ("Depth", "0"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, status);
    }

    [TestMethod]
    public async Task ThunderbirdAutoconfigIncludesMailAndDav()
    {
        var c = await TestHost.ClientAsync();
        var resp = await c.GetAsync("/mail/config-v1.1.xml?emailaddress=carol@example.com");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.AreEqual("127.0.0.1", doc.Descendants("incomingServer").Single().Element("hostname")!.Value);
        Assert.AreEqual("STARTTLS", doc.Descendants("outgoingServer").Single().Element("socketType")!.Value);
        Assert.AreEqual("https://dav.example.com/dav/", doc.Descendants("calendar").Single().Element("serverURL")!.Value);
        Assert.AreEqual("https://dav.example.com/dav/", doc.Descendants("addressBook").Single().Element("serverURL")!.Value);

        Assert.AreEqual(HttpStatusCode.OK, (await c.GetAsync("/.well-known/autoconfig/mail/config-v1.1.xml")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.GetAsync("/mail/config-v1.1.xml?emailaddress=x@other.org")).StatusCode);
    }

    [TestMethod]
    public async Task OutlookAutodiscover()
    {
        var c = await TestHost.ClientAsync();
        const string request = """
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006">
              <Request>
                <EMailAddress>carol@example.com</EMailAddress>
                <AcceptableResponseSchema>http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a</AcceptableResponseSchema>
              </Request>
            </Autodiscover>
            """;
        var resp = await c.PostAsync("/Autodiscover/Autodiscover.xml", new StringContent(request, Encoding.UTF8, "text/xml"));
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "<Type>IMAP</Type>");
        StringAssert.Contains(body, "<LoginName>carol@example.com</LoginName>");
        StringAssert.Contains(body, "<Type>SMTP</Type>");

        var json = await c.GetStringAsync("/autodiscover/autodiscover.json/v1.0/carol@example.com?Protocol=AutodiscoverV1");
        StringAssert.Contains(json, "\"Url\":\"");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await c.GetAsync("/autodiscover/autodiscover.json/v1.0/carol@example.com?Protocol=ActiveSync")).StatusCode);
    }

    [TestMethod]
    public void AppleProfileContainsAccounts()
    {
        var user = new User { Username = "carol", DisplayName = "Carol", Email = "carol@example.com" };
        var settings = new ServerSettings { ImapHost = "imap.example.com", SmtpHost = "smtp.example.com", MailProviderName = "Example" };
        var xml = Autoconfig.AppleProfile(user, "https://dav.example.com", settings);
        StringAssert.Contains(xml, "com.apple.caldav.account");
        StringAssert.Contains(xml, "com.apple.carddav.account");
        StringAssert.Contains(xml, "com.apple.mail.managed");
        StringAssert.Contains(xml, "<string>/dav/principals/carol/</string>");
    }
}
