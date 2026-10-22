using System.Net.Mail;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

/// <summary>
/// Client auto-configuration metadata: Mozilla autoconfig (Thunderbird, incl. CalDAV/CardDAV),
/// Microsoft Autodiscover (POX v1 and the JSON v2 redirector) and Apple configuration profiles.
/// </summary>
public static class Autoconfig
{
    private static readonly XNamespace AdResponse = "http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006";
    private static readonly XNamespace AdOutlook = "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a";

    public static void MapAutoconfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/autoconfig/mail/config-v1.1.xml", MozillaAsync).AllowAnonymous();
        app.MapGet("/mail/config-v1.1.xml", MozillaAsync).AllowAnonymous();
        app.MapMethods("/autodiscover/autodiscover.xml", ["GET", "POST"], AutodiscoverXmlAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/autodiscover/autodiscover.json", AutodiscoverJson).AllowAnonymous();
        app.MapGet("/autodiscover/autodiscover.json/v1.0/{**email}", AutodiscoverJson).AllowAnonymous();
    }

    /// <summary>Base URL clients should use for this server (configured public URL, else the request URL).</summary>
    public static string PublicBaseUrl(HttpRequest request, ServerSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.PublicUrl)
            ? settings.PublicUrl.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}";

    public static string DavUrl(HttpRequest request, ServerSettings settings) => PublicBaseUrl(request, settings) + "/dav/";

    private static bool DomainAllowed(ServerSettings s, string? email)
    {
        if (string.IsNullOrWhiteSpace(s.MailDomains)) return true;
        var at = email?.LastIndexOf('@') ?? -1;
        if (email == null || at < 0) return true; // no address supplied: describe the default domain
        var domain = email[(at + 1)..].Trim();
        return s.MailDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254) return null;
        return MailAddress.TryCreate(email.Trim(), out var addr) ? addr.Address : null;
    }

    private static string SocketType(MailSecurity s) => s switch
    {
        MailSecurity.Tls => "SSL",
        MailSecurity.StartTls => "STARTTLS",
        _ => "plain",
    };

    private static IResult Xml(XDocument doc, string contentType = "application/xml")
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(w);
        return Results.Text(sb.ToString(), contentType + "; charset=utf-8", Encoding.UTF8);
    }

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    // ------------------------------------------------------------------ Mozilla / Thunderbird

    private static IResult MozillaAsync(HttpRequest request, SettingsService settingsService, string? emailaddress)
    {
        var s = settingsService.Current;
        if (!s.AutoconfigEnabled || !s.ImapConfigured) return Results.NotFound();
        var email = ValidEmail(emailaddress);
        if (!DomainAllowed(s, email)) return Results.NotFound();

        var domain = email?[(email.LastIndexOf('@') + 1)..]
                     ?? s.MailDomains?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                     ?? request.Host.Host;
        var name = string.IsNullOrWhiteSpace(s.MailProviderName) ? domain : s.MailProviderName;
        var dav = DavUrl(request, s);

        var provider = new XElement("emailProvider", new XAttribute("id", domain),
            (s.MailDomains?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [domain])
                .Select(d => new XElement("domain", d)),
            new XElement("displayName", name),
            new XElement("displayShortName", name),
            new XElement("incomingServer", new XAttribute("type", "imap"),
                new XElement("hostname", s.ImapHost),
                new XElement("port", s.ImapPort),
                new XElement("socketType", SocketType(s.ImapSecurity)),
                new XElement("authentication", "password-cleartext"),
                new XElement("username", "%EMAILADDRESS%")));
        if (!string.IsNullOrWhiteSpace(s.SmtpHost))
        {
            provider.Add(new XElement("outgoingServer", new XAttribute("type", "smtp"),
                new XElement("hostname", s.SmtpHost),
                new XElement("port", s.SmtpPort),
                new XElement("socketType", SocketType(s.SmtpSecurity)),
                new XElement("authentication", "password-cleartext"),
                new XElement("username", "%EMAILADDRESS%")));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("clientConfig", new XAttribute("version", "1.1"),
                provider,
                new XElement("addressBook", new XAttribute("type", "carddav"),
                    new XElement("username", "%EMAILADDRESS%"),
                    new XElement("authentication", "http-basic"),
                    new XElement("serverURL", dav)),
                new XElement("calendar", new XAttribute("type", "caldav"),
                    new XElement("username", "%EMAILADDRESS%"),
                    new XElement("authentication", "http-basic"),
                    new XElement("serverURL", dav))));
        return Xml(doc);
    }

    // ------------------------------------------------------------------ Microsoft Autodiscover (POX)

    private static async Task<IResult> AutodiscoverXmlAsync(HttpRequest request, SettingsService settingsService)
    {
        var s = settingsService.Current;
        if (!s.AutoconfigEnabled || !s.ImapConfigured) return Results.NotFound();

        string? email = null;
        string? schema = null;
        if (HttpMethods.IsPost(request.Method))
        {
            try
            {
                using var reader = XmlReader.Create(request.Body, new XmlReaderSettings
                {
                    Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536,
                });
                var doc = await XDocument.LoadAsync(reader, LoadOptions.None, request.HttpContext.RequestAborted);
                email = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "EMailAddress")?.Value;
                schema = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AcceptableResponseSchema")?.Value;
            }
            catch (XmlException)
            {
                return AutodiscoverError(600, "Invalid Request");
            }
        }
        email = ValidEmail(email ?? request.Query["emailaddress"]);

        // Exchange ActiveSync and other schemas are not offered; let the client fall back to manual setup.
        if (schema != null && !schema.Contains("outlook/responseschema", StringComparison.OrdinalIgnoreCase))
            return AutodiscoverError(600, "Invalid Request");
        if (email == null || !DomainAllowed(s, email)) return AutodiscoverError(600, "Invalid Request");

        var account = new XElement(AdOutlook + "Account",
            new XElement(AdOutlook + "AccountType", "email"),
            new XElement(AdOutlook + "Action", "settings"),
            Protocol("IMAP", s.ImapHost!, s.ImapPort, s.ImapSecurity, email));
        if (!string.IsNullOrWhiteSpace(s.SmtpHost))
        {
            var smtp = Protocol("SMTP", s.SmtpHost, s.SmtpPort, s.SmtpSecurity, email);
            smtp.Add(new XElement(AdOutlook + "UsePOPAuth", "on"), new XElement(AdOutlook + "SMTPLast", "off"));
            account.Add(smtp);
        }

        var response = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(AdResponse + "Autodiscover",
                new XElement(AdOutlook + "Response",
                    new XElement(AdOutlook + "User", new XElement(AdOutlook + "DisplayName", email)),
                    account)));
        return Xml(response, "text/xml");
    }

    private static XElement Protocol(string type, string host, int port, MailSecurity security, string login) =>
        new(AdOutlook + "Protocol",
            new XElement(AdOutlook + "Type", type),
            new XElement(AdOutlook + "Server", host),
            new XElement(AdOutlook + "Port", port),
            new XElement(AdOutlook + "DomainRequired", "off"),
            new XElement(AdOutlook + "LoginName", login),
            new XElement(AdOutlook + "SPA", "off"),
            new XElement(AdOutlook + "SSL", security == MailSecurity.None ? "off" : "on"),
            new XElement(AdOutlook + "Encryption", security switch { MailSecurity.Tls => "SSL", MailSecurity.StartTls => "TLS", _ => "None" }),
            new XElement(AdOutlook + "AuthRequired", "on"));

    private static IResult AutodiscoverError(int code, string message)
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(AdResponse + "Autodiscover",
                new XElement(AdResponse + "Response",
                    new XElement(AdResponse + "Error",
                        new XAttribute("Time", DateTime.UtcNow.ToString("HH:mm:ss.fffffff")),
                        new XAttribute("Id", "1"),
                        new XElement(AdResponse + "ErrorCode", code),
                        new XElement(AdResponse + "Message", message),
                        new XElement(AdResponse + "DebugData")))));
        return Xml(doc, "text/xml");
    }

    // ------------------------------------------------------------------ Autodiscover v2 (JSON redirector)

    private static readonly System.Text.Json.JsonSerializerOptions PascalCase = new();

    private static IResult AutodiscoverJson(HttpRequest request, SettingsService settingsService, string? protocol)
    {
        var s = settingsService.Current;
        if (!s.AutoconfigEnabled || !s.ImapConfigured) return Results.NotFound();
        if (!string.Equals(protocol, "AutodiscoverV1", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ErrorCode = "ProtocolNotSupported", ErrorMessage = "The given protocol value is invalid or not supported." },
                PascalCase, statusCode: 400);
        return Results.Json(new
        {
            Protocol = "AutodiscoverV1",
            Url = $"{request.Scheme}://{request.Host}{request.PathBase}/autodiscover/autodiscover.xml",
        }, PascalCase);
    }

    // ------------------------------------------------------------------ Apple configuration profile

    /// <summary>Builds an unsigned .mobileconfig with CalDAV, CardDAV and (if configured) IMAP accounts.</summary>
    public static string AppleProfile(User user, string publicBaseUrl, ServerSettings s)
    {
        var uri = new Uri(publicBaseUrl);
        var useSsl = uri.Scheme == "https";
        var principal = new Dav.DavPaths(uri.AbsolutePath.TrimEnd('/') + "/dav").Principal(user.Username);
        var login = string.IsNullOrEmpty(user.Email) ? user.Username : user.Email;
        var orgName = string.IsNullOrWhiteSpace(s.MailProviderName) ? uri.Host : s.MailProviderName;
        var idBase = "network.nullify.caldav." + string.Join('.', uri.Host.Split('.').Reverse());

        static XElement Key(string k) => new("key", k);
        static XElement Str(string v) => new("string", v);
        static XElement Int(int v) => new("integer", v);
        static XElement Bool(bool v) => new(v ? "true" : "false");

        XElement Payload(string type, string id, string display, params XElement[] items) =>
            new("dict",
                Key("PayloadType"), Str(type),
                Key("PayloadIdentifier"), Str($"{idBase}.{id}.{user.Username}"),
                Key("PayloadUUID"), Str(Guid.NewGuid().ToString().ToUpperInvariant()),
                Key("PayloadVersion"), Int(1),
                Key("PayloadDisplayName"), Str(display),
                items);

        var payloads = new List<XElement>
        {
            Payload("com.apple.caldav.account", "caldav", "Calendars",
                Key("CalDAVAccountDescription"), Str($"{orgName} Calendars"),
                Key("CalDAVHostName"), Str(uri.Host),
                Key("CalDAVPort"), Int(uri.Port),
                Key("CalDAVPrincipalURL"), Str(principal),
                Key("CalDAVUseSSL"), Bool(useSsl),
                Key("CalDAVUsername"), Str(user.Username)),
            Payload("com.apple.carddav.account", "carddav", "Contacts",
                Key("CardDAVAccountDescription"), Str($"{orgName} Contacts"),
                Key("CardDAVHostName"), Str(uri.Host),
                Key("CardDAVPort"), Int(uri.Port),
                Key("CardDAVPrincipalURL"), Str(principal),
                Key("CardDAVUseSSL"), Bool(useSsl),
                Key("CardDAVUsername"), Str(user.Username)),
        };

        if (s.ImapConfigured && !string.IsNullOrEmpty(user.Email))
        {
            var mail = new List<XElement>
            {
                Key("EmailAccountDescription"), Str($"{orgName} Mail"),
                Key("EmailAccountName"), Str(user.DisplayName),
                Key("EmailAccountType"), Str("EmailTypeIMAP"),
                Key("EmailAddress"), Str(user.Email),
                Key("IncomingMailServerAuthentication"), Str("EmailAuthPassword"),
                Key("IncomingMailServerHostName"), Str(s.ImapHost!),
                Key("IncomingMailServerPortNumber"), Int(s.ImapPort),
                Key("IncomingMailServerUseSSL"), Bool(s.ImapSecurity != MailSecurity.None),
                Key("IncomingMailServerUsername"), Str(CredentialService.ImapLoginFor(user)),
            };
            if (!string.IsNullOrWhiteSpace(s.SmtpHost))
            {
                mail.AddRange([
                    Key("OutgoingMailServerAuthentication"), Str("EmailAuthPassword"),
                    Key("OutgoingMailServerHostName"), Str(s.SmtpHost),
                    Key("OutgoingMailServerPortNumber"), Int(s.SmtpPort),
                    Key("OutgoingMailServerUseSSL"), Bool(s.SmtpSecurity != MailSecurity.None),
                    Key("OutgoingMailServerUsername"), Str(CredentialService.ImapLoginFor(user)),
                    Key("OutgoingPasswordSameAsIncomingPassword"), Bool(true),
                ]);
            }
            payloads.Add(Payload("com.apple.mail.managed", "mail", "Mail", mail.ToArray()));
        }

        var root = new XElement("plist", new XAttribute("version", "1.0"),
            new XElement("dict",
                Key("PayloadType"), Str("Configuration"),
                Key("PayloadIdentifier"), Str($"{idBase}.profile.{user.Username}"),
                Key("PayloadUUID"), Str(Guid.NewGuid().ToString().ToUpperInvariant()),
                Key("PayloadVersion"), Int(1),
                Key("PayloadDisplayName"), Str($"{orgName} ({login})"),
                Key("PayloadDescription"), Str("Calendar, contacts and mail settings"),
                Key("PayloadOrganization"), Str(orgName),
                Key("PayloadRemovalDisallowed"), Bool(false),
                Key("PayloadContent"), new XElement("array", payloads)));

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append(root.ToString(SaveOptions.None));
        return sb.ToString();
    }
}
