// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.SyncMl;

public enum DbKind { Contacts, Calendar, Events, Tasks, Notes }

public enum SyncPhase { Init, ClientSync, Mapping, Done }

/// <summary>State of one client datastore within a session.</summary>
public sealed class DbSession
{
    public required string ClientUri { get; init; }
    public required string ServerUri { get; init; }
    public required DbKind Kind { get; init; }
    public required int CollectionId { get; init; }
    public required string[] Components { get; init; }
    public bool CanWrite { get; init; }
    public int AlertCode { get; set; }
    public string? ClientNext { get; set; }
    public string ServerNext { get; set; } = "";
    public int AnchorId { get; set; }
    public long BaseToken { get; set; }
    public Dictionary<string, string> LuidToUri { get; } = new();
    public HashSet<string> Touched { get; } = [];
    public Dictionary<string, string>? SlowIndex { get; set; }
    public string? IncomingType { get; set; }
    public bool ServerChangesQueued { get; set; }

    public bool IsContacts => Kind == DbKind.Contacts;
    public bool IsSlowOrRefresh => AlertCode is 201 or 203 or 205;
}

public sealed class SyncMlSession(string key)
{
    public string Key { get; } = key;
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public int? DeviceRowId { get; set; }
    public string DeviceId { get; set; } = "";
    public string ServerUri { get; set; } = "";
    public string Version { get; set; } = "1.2";
    public int ServerMsgId { get; set; }
    public int MaxMsgSize { get; set; } = 64 * 1024;
    public SyncPhase Phase { get; set; } = SyncPhase.Init;
    public bool ClientSyncSeen { get; set; }
    public bool DevInfRequested { get; set; }
    public List<DbSession> Databases { get; } = [];
    public List<(DbSession Db, XElement? Command)> Outbox { get; } = [];
    public Dictionary<string, string> ClientPreferred { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SyncMlSessionStore
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SyncMlSession> _sessions = new();

    public SyncMlSession GetOrCreate(string key)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions.Where(kv => now - kv.Value.LastActivity > Expiry).ToList())
            _sessions.TryRemove(kv.Key, out _);
        var session = _sessions.GetOrAdd(key, k => new SyncMlSession(k));
        session.LastActivity = now;
        return session;
    }

    public void Remove(string key) => _sessions.TryRemove(key, out _);
}

/// <summary>HTTP binding for SyncML (OMA DS 1.1 / 1.2) in XML and WBXML representation.</summary>
public static class SyncMlEndpoint
{
    public const string XmlType = "application/vnd.syncml+xml";
    public const string WbxmlType = "application/vnd.syncml+wbxml";
    private const int MaxBody = 4 * 1024 * 1024;

    public static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var settings = services.GetRequiredService<SettingsService>().Current;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Nullify.CalDav.SyncMl");
        if (!settings.SyncMlEnabled)
        {
            http.Response.StatusCode = 404;
            return;
        }
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("Nullify.CalDav SyncML endpoint. Configure your SyncML client to POST here.\n");
            return;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxBody)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
        }
        var body = ms.ToArray();
        var isWbxml = (http.Request.ContentType ?? "").Contains("wbxml", StringComparison.OrdinalIgnoreCase) ||
                      ((http.Request.ContentType ?? "").Length == 0 && Wbxml.LooksLikeWbxml(body));

        XElement message;
        try
        {
            if (isWbxml) message = Wbxml.Decode(body, out _);
            else
            {
                using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxBody * 2,
                });
                message = SyncMlXml.StripNamespaces(XDocument.Load(reader).Root!);
            }
        }
        catch (Exception ex) when (ex is XmlException or FormatException or EndOfStreamException or InvalidOperationException)
        {
            log.LogInformation("Rejected malformed SyncML message: {Message}", ex.Message);
            http.Response.StatusCode = 400;
            return;
        }
        if (message.Name.LocalName != "SyncML" || message.Element("SyncHdr") == null)
        {
            http.Response.StatusCode = 400;
            return;
        }

        var hdr = message.Element("SyncHdr")!;
        var key = (hdr.Element("Source")?.Element("LocURI")?.Value ?? "") + "|" + (hdr.Element("SessionID")?.Value ?? "");
        var store = services.GetRequiredService<SyncMlSessionStore>();
        var session = store.GetOrCreate(key);

        XElement response;
        await session.Lock.WaitAsync(http.RequestAborted);
        try
        {
            var processor = new SyncMlProcessor(http, session, log);
            response = await processor.ProcessAsync(message);
            if (session.Phase == SyncPhase.Done) store.Remove(key);
        }
        finally
        {
            session.Lock.Release();
        }

        byte[] output;
        if (isWbxml)
        {
            output = Wbxml.Encode(response, Wbxml.Language.SyncMl,
                session.Version == "1.1" ? Wbxml.PublicIdSyncMl11 : session.Version == "1.0" ? Wbxml.PublicIdSyncMl10 : Wbxml.PublicIdSyncMl12);
            http.Response.ContentType = WbxmlType;
        }
        else
        {
            output = SerializeXml(response, session.Version);
            http.Response.ContentType = XmlType + "; charset=UTF-8";
        }
        http.Response.ContentLength = output.Length;
        await http.Response.Body.WriteAsync(output, http.RequestAborted);
    }

    public static byte[] SerializeXml(XElement message, string version)
    {
        var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings
               {
                   Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize, Indent = false,
               }))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), SyncMlXml.AddNamespaces(message, version)).Save(w);
        }
        return ms.ToArray();
    }
}

/// <summary>Processes one SyncML message within a session and builds the reply.</summary>
public sealed class SyncMlProcessor
{
    private readonly HttpContext _http;
    private readonly SyncMlSession _session;
    private readonly ILogger _log;
    private readonly AppDbContext _db;
    private readonly WriteGate _gate;
    private User? _user;

    private readonly List<XElement> _out = [];
    private int _cmdId;
    private string _clientMsgId = "1";
    private int _budget;

    public SyncMlProcessor(HttpContext http, SyncMlSession session, ILogger log)
    {
        _http = http;
        _session = session;
        _log = log;
        _db = http.RequestServices.GetRequiredService<AppDbContext>();
        _gate = http.RequestServices.GetRequiredService<WriteGate>();
    }

    private CalendarStore Calendars => _http.RequestServices.GetRequiredService<CalendarStore>();
    private AddressBookStore Books => _http.RequestServices.GetRequiredService<AddressBookStore>();

    private static XElement E(string name, params object?[] content) => new(name, content);
    private static string? Loc(XElement? e, string child) => e?.Element(child)?.Element("LocURI")?.Value.Trim();
    private string NextCmdId() => (++_cmdId).ToString();

    private static string Norm(string uri)
    {
        var u = uri.Trim();
        if (u.StartsWith("./", StringComparison.Ordinal)) u = u[2..];
        var q = u.IndexOf('?');
        if (q >= 0) u = u[..q];
        return u.Trim('/');
    }

    public async Task<XElement> ProcessAsync(XElement message)
    {
        var hdr = message.Element("SyncHdr")!;
        var body = message.Element("SyncBody") ?? new XElement("SyncBody");
        _clientMsgId = hdr.Element("MsgID")?.Value.Trim() ?? "1";
        var verDtd = hdr.Element("VerDTD")?.Value.Trim();
        if (verDtd is "1.0" or "1.1" or "1.2") _session.Version = verDtd;
        _session.DeviceId = Loc(hdr, "Source") ?? "";
        _session.ServerUri = Loc(hdr, "Target") ?? "";
        if (int.TryParse(hdr.Element("Meta")?.Element("MaxMsgSize")?.Value, out var max) && max > 1024) _session.MaxMsgSize = max;
        _budget = Math.Max(2048, (int)(_session.MaxMsgSize * 0.85) - 1024);

        // ---------------------------------------------------------- authentication
        int headerStatus;
        if (_session.UserId == null)
        {
            var (user, status) = await AuthenticateAsync(hdr.Element("Cred"));
            if (user == null)
            {
                var chal = E("Chal", E("Meta", E("Type", "syncml:auth-basic"), E("Format", "b64")));
                _out.Add(Status("SyncHdr", "0", _session.ServerUri, _session.DeviceId, status, chal));
                return Build(hdr, final: true);
            }
            _session.UserId = user.Id;
            _user = user;
            headerStatus = 212;
            await LoadDeviceAsync();
        }
        else
        {
            _user = await _db.Users.FirstOrDefaultAsync(u => u.Id == _session.UserId && u.IsEnabled);
            if (_user == null)
            {
                _session.Phase = SyncPhase.Done;
                _out.Add(Status("SyncHdr", "0", _session.ServerUri, _session.DeviceId, 401));
                return Build(hdr, final: true);
            }
            headerStatus = 200;
        }
        _out.Add(Status("SyncHdr", "0", _session.ServerUri, _session.DeviceId, headerStatus));

        // ---------------------------------------------------------- commands
        var nextMessageRequested = false;
        var alertsSeen = false;
        foreach (var cmd in body.Elements())
        {
            var name = cmd.Name.LocalName;
            var cmdId = cmd.Element("CmdID")?.Value.Trim() ?? "0";
            switch (name)
            {
                case "Final":
                case "Status":
                    break;
                case "Alert":
                {
                    var code = int.TryParse(cmd.Element("Data")?.Value.Trim(), out var c) ? c : 0;
                    if (code == 222) { nextMessageRequested = true; _out.Add(Status("Alert", cmdId, null, null, 200)); }
                    else if (code is >= 200 and <= 205) { alertsSeen = true; await ProcessSyncAlertAsync(cmd, cmdId, code); }
                    else _out.Add(Status("Alert", cmdId, null, null, 200));
                    break;
                }
                case "Put":
                    foreach (var item in cmd.Elements("Item")) ParseDevInf(ExtractDevInf(item));
                    await SaveDeviceAsync();
                    _out.Add(Status("Put", cmdId, null, cmd.Element("Item") is { } pi ? Loc(pi, "Source") : null, 200));
                    break;
                case "Results":
                    foreach (var item in cmd.Elements("Item")) ParseDevInf(ExtractDevInf(item));
                    await SaveDeviceAsync();
                    _out.Add(Status("Results", cmdId, null, null, 200));
                    break;
                case "Get":
                {
                    var target = cmd.Element("Item") is { } gi ? Loc(gi, "Target") : null;
                    if (target != null && Norm(target).StartsWith("devinf", StringComparison.OrdinalIgnoreCase))
                    {
                        _out.Add(Status("Get", cmdId, target, null, 200));
                        _out.Add(E("Results", E("CmdID", NextCmdId()), E("MsgRef", _clientMsgId), E("CmdRef", cmdId),
                            E("Meta", E("Type", "application/vnd.syncml-devinf+" + (_http.Request.ContentType?.Contains("wbxml") == true ? "wbxml" : "xml"))),
                            E("Item", E("Source", E("LocURI", target)), E("Data", ServerDevInf()))));
                    }
                    else _out.Add(Status("Get", cmdId, target, null, 404));
                    break;
                }
                case "Sync":
                    _session.ClientSyncSeen = true;
                    await ProcessSyncAsync(cmd, cmdId);
                    break;
                case "Map":
                    await ProcessMapAsync(cmd, cmdId);
                    break;
                default:
                    _out.Add(Status(name, cmdId, null, null, 406));
                    break;
            }
        }

        var final = body.Element("Final") != null;

        // ---------------------------------------------------------- package flow
        if (final && _session.Phase == SyncPhase.Init && alertsSeen)
        {
            _session.Phase = SyncPhase.ClientSync;
            if (_session.ClientPreferred.Count == 0 && !_session.DevInfRequested)
            {
                _session.DevInfRequested = true;
                var devinf = _session.Version == "1.1" ? "./devinf11" : _session.Version == "1.0" ? "./devinf10" : "./devinf12";
                _out.Add(E("Get", E("CmdID", NextCmdId()), E("Meta", E("Type", "application/vnd.syncml-devinf+xml")),
                    E("Item", E("Target", E("LocURI", devinf)))));
            }
            if (!_session.ClientSyncSeen) return Build(hdr, final: true);
        }

        if (final && _session.Phase == SyncPhase.ClientSync)
        {
            foreach (var dbs in _session.Databases.Where(d => !d.ServerChangesQueued))
                await QueueServerChangesAsync(dbs);
            _session.Phase = SyncPhase.Mapping;
            var done = FlushOutbox();
            return Build(hdr, final: done);
        }

        if (_session.Phase == SyncPhase.Mapping && (nextMessageRequested || final))
        {
            if (_session.Outbox.Count > 0)
            {
                var done = FlushOutbox();
                return Build(hdr, final: done);
            }
            if (final)
            {
                _session.Phase = SyncPhase.Done;
                await SaveDeviceAsync();
                return Build(hdr, final: true);
            }
            return Build(hdr, final: true);
        }

        if (!final)
        {
            // The client has more to send in this package.
            _out.Add(E("Alert", E("CmdID", NextCmdId()), E("Data", "222"),
                E("Item", E("Target", E("LocURI", _session.DeviceId)), E("Source", E("LocURI", _session.ServerUri)))));
            return Build(hdr, final: false);
        }

        return Build(hdr, final: true);
    }

    private XElement Build(XElement clientHdr, bool final)
    {
        var hdr = E("SyncHdr",
            E("VerDTD", _session.Version),
            E("VerProto", "SyncML/" + _session.Version),
            E("SessionID", clientHdr.Element("SessionID")?.Value ?? "1"),
            E("MsgID", (++_session.ServerMsgId).ToString()),
            E("Target", E("LocURI", _session.DeviceId)),
            E("Source", E("LocURI", _session.ServerUri)));
        var body = E("SyncBody", _out);
        if (final) body.Add(E("Final"));
        return E("SyncML", hdr, body);
    }

    private XElement Status(string cmd, string cmdRef, string? targetRef, string? sourceRef, int code, XElement? chal = null, XElement? item = null) =>
        E("Status",
            E("CmdID", NextCmdId()),
            E("MsgRef", _clientMsgId),
            E("CmdRef", cmdRef),
            E("Cmd", cmd),
            targetRef == null ? null : E("TargetRef", targetRef),
            sourceRef == null ? null : E("SourceRef", sourceRef),
            chal,
            E("Data", code.ToString()),
            item);

    // ------------------------------------------------------------------ Authentication & device

    private async Task<(User? User, int Status)> AuthenticateAsync(XElement? cred)
    {
        string? login = null, password = null;
        if (cred != null)
        {
            var type = cred.Element("Meta")?.Element("Type")?.Value.Trim() ?? "syncml:auth-basic";
            if (!type.Equals("syncml:auth-basic", StringComparison.OrdinalIgnoreCase)) return (null, 401);
            var data = cred.Element("Data")?.Value.Trim() ?? "";
            try { data = Encoding.UTF8.GetString(Convert.FromBase64String(data)); }
            catch (FormatException) { return (null, 401); }
            var colon = data.IndexOf(':');
            if (colon <= 0) return (null, 401);
            login = data[..colon];
            password = data[(colon + 1)..];
        }
        else
        {
            var header = _http.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                    var colon = decoded.IndexOf(':');
                    if (colon > 0) { login = decoded[..colon]; password = decoded[(colon + 1)..]; }
                }
                catch (FormatException) { }
            }
        }
        if (login == null || password == null) return (null, 407);

        var validator = _http.RequestServices.GetRequiredService<DavCredentialValidator>();
        var key = "syncml:" + (_http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var user = await validator.ValidateAsync(_db, login, password, key, _http.RequestAborted);
        return user == null ? (null, 401) : (user, 212);
    }

    private async Task LoadDeviceAsync()
    {
        var device = await _db.SyncMlDevices.FirstOrDefaultAsync(d => d.UserId == _session.UserId && d.DeviceId == _session.DeviceId);
        if (device == null)
        {
            using (await _gate.EnterAsync())
            {
                device = new SyncMlDevice { UserId = _session.UserId!.Value, DeviceId = _session.DeviceId };
                _db.SyncMlDevices.Add(device);
                await _db.SaveChangesAsync();
            }
        }
        _session.DeviceRowId = device.Id;
        foreach (var line in (device.PreferredTypes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) _session.ClientPreferred[line[..eq]] = line[(eq + 1)..].Trim();
        }
    }

    private string? _man, _mod, _swv;

    private async Task SaveDeviceAsync()
    {
        if (_session.DeviceRowId == null) return;
        using (await _gate.EnterAsync())
        {
            var device = await _db.SyncMlDevices.FirstAsync(d => d.Id == _session.DeviceRowId);
            device.LastSyncUtc = DateTime.UtcNow;
            if (_man != null) device.Manufacturer = _man;
            if (_mod != null) device.Model = _mod;
            if (_swv != null) device.SoftwareVersion = _swv;
            if (_session.ClientPreferred.Count > 0)
                device.PreferredTypes = string.Join('\n', _session.ClientPreferred.Select(kv => $"{kv.Key}={kv.Value}"));
            await _db.SaveChangesAsync();
        }
    }

    private static XElement? ExtractDevInf(XElement item)
    {
        var data = item.Element("Data");
        if (data == null) return null;
        if (data.Element("DevInf") is { } inline) return inline;
        var text = data.Value.Trim();
        if (!text.StartsWith('<')) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return SyncMlXml.StripNamespaces(XDocument.Load(reader).Root!);
        }
        catch (XmlException) { return null; }
    }

    private void ParseDevInf(XElement? devInf)
    {
        if (devInf == null) return;
        _man = devInf.Element("Man")?.Value.Trim() ?? _man;
        _mod = devInf.Element("Mod")?.Value.Trim() ?? _mod;
        _swv = devInf.Element("SwV")?.Value.Trim() ?? _swv;
        foreach (var ds in devInf.Elements("DataStore"))
        {
            var source = ds.Element("SourceRef")?.Value;
            var pref = ds.Element("Rx-Pref")?.Element("CTType")?.Value.Trim();
            if (source != null && !string.IsNullOrEmpty(pref)) _session.ClientPreferred[Norm(source)] = pref;
        }
    }

    private XElement ServerDevInf()
    {
        XElement Store(string source, string display, params (string Type, string Ver)[] types) =>
            E("DataStore",
                E("SourceRef", source),
                E("DisplayName", display),
                E("MaxGUIDSize", "64"),
                E("Rx-Pref", E("CTType", types[0].Type), E("VerCT", types[0].Ver)),
                types.Skip(1).Select(t => E("Rx", E("CTType", t.Type), E("VerCT", t.Ver))),
                E("Tx-Pref", E("CTType", types[0].Type), E("VerCT", types[0].Ver)),
                types.Skip(1).Select(t => E("Tx", E("CTType", t.Type), E("VerCT", t.Ver))),
                E("SyncCap", Enumerable.Range(1, 7).Select(i => E("SyncType", i.ToString()))));

        return E("DevInf",
            E("VerDTD", _session.Version == "1.2" ? "1.2" : "1.1"),
            E("Man", "Nullify"),
            E("Mod", "Nullify.CalDav"),
            E("OEM", "Nullify"),
            E("FwV", "1.0"),
            E("SwV", typeof(SyncMlProcessor).Assembly.GetName().Version?.ToString() ?? "1.0"),
            E("HwV", "1.0"),
            E("DevID", string.IsNullOrEmpty(_session.ServerUri) ? "Nullify.CalDav" : _session.ServerUri),
            E("DevTyp", "server"),
            E("UTC"),
            E("SupportNumberOfChanges"),
            Store("contacts", "Contacts", ("text/x-vcard", "2.1"), ("text/vcard", "3.0")),
            Store("calendar", "Calendar", ("text/x-vcalendar", "1.0"), ("text/calendar", "2.0")),
            Store("events", "Events", ("text/x-vcalendar", "1.0"), ("text/calendar", "2.0")),
            Store("tasks", "Tasks", ("text/x-vcalendar", "1.0"), ("text/calendar", "2.0")),
            Store("notes", "Notes", ("text/plain", "1.0"), ("text/x-vnote", "1.1"), ("text/calendar", "2.0")));
    }

    // ------------------------------------------------------------------ Database resolution

    private static DbKind? KindOf(string name) => name.ToLowerInvariant() switch
    {
        "contacts" or "contact" or "card" or "cards" or "addressbook" or "scard" or "pab" or "vcard" => DbKind.Contacts,
        "calendar" or "cal" or "agenda" or "scal" or "vcalendar" => DbKind.Calendar,
        "event" or "events" => DbKind.Events,
        "task" or "tasks" or "todo" or "todos" or "stask" => DbKind.Tasks,
        "note" or "notes" or "memo" or "memos" or "snote" or "journal" => DbKind.Notes,
        _ => null,
    };

    private async Task<DbSession?> ResolveAsync(string serverUri, string clientUri)
    {
        var norm = Norm(serverUri);
        var parts = norm.Split('/', 2);
        if (parts[0].Equals("pim", StringComparison.OrdinalIgnoreCase) && parts.Length == 2) parts = parts[1].Split('/', 2);
        var kind = KindOf(parts[0]);
        if (kind == null) return null;
        var collection = parts.Length > 1 ? parts[1] : null;
        var resolver = new DavResolver(_db, _user!, new DavPaths(""));

        if (kind == DbKind.Contacts)
        {
            AddressBookNode? book;
            if (collection != null) book = await resolver.LoadAddressBookAsync(collection, _http.RequestAborted);
            else
            {
                var first = await _db.AddressBooks.Where(b => b.OwnerId == _user!.Id).OrderBy(b => b.Id).Select(b => b.Uri).FirstOrDefaultAsync();
                if (first == null)
                {
                    using (await _gate.EnterAsync())
                        first = (await Books.CreateAsync(_user!.Id, "default", "Contacts")).Uri;
                }
                book = await resolver.LoadAddressBookAsync(first, _http.RequestAborted);
            }
            if (book == null) return null;
            return new DbSession
            {
                ClientUri = clientUri, ServerUri = serverUri, Kind = kind.Value, CollectionId = book.Book.Id,
                Components = [], CanWrite = book.CanWrite,
            };
        }

        var required = kind switch { DbKind.Tasks => "VTODO", DbKind.Notes => "VJOURNAL", _ => "VEVENT" };
        CalendarNode? cal;
        if (collection != null) cal = await resolver.LoadCalendarAsync(collection, _http.RequestAborted);
        else
        {
            var owned = await _db.Calendars.Where(c => c.OwnerId == _user!.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
            var match = owned.FirstOrDefault(c => c.ComponentList.Contains(required));
            if (match == null)
            {
                var store = Calendars;
                using (await _gate.EnterAsync())
                {
                    var name = kind switch { DbKind.Tasks => "Tasks", DbKind.Notes => "Notes", _ => "Calendar" };
                    var uri = await store.UniqueHomeUriAsync(_user!.Id, name.ToLowerInvariant());
                    match = await store.CreateAsync(_user.Id, uri, name, [required]);
                }
            }
            cal = await resolver.LoadCalendarAsync(match.Uri, _http.RequestAborted);
        }
        if (cal == null || !cal.Calendar.ComponentList.Contains(required)) return null;

        string[] comps = kind switch
        {
            DbKind.Calendar => cal.Calendar.ComponentList.Where(c => c is "VEVENT" or "VTODO").ToArray(),
            _ => [required],
        };
        return new DbSession
        {
            ClientUri = clientUri, ServerUri = serverUri, Kind = kind.Value, CollectionId = cal.Calendar.Id,
            Components = comps, CanWrite = cal.CanWrite,
        };
    }

    private async Task<long> CurrentTokenAsync(DbSession db) => db.IsContacts
        ? await _db.AddressBooks.Where(b => b.Id == db.CollectionId).Select(b => b.SyncToken).FirstAsync()
        : await _db.Calendars.Where(c => c.Id == db.CollectionId).Select(c => c.SyncToken).FirstAsync();

    // ------------------------------------------------------------------ Alert

    private async Task ProcessSyncAlertAsync(XElement cmd, string cmdId, int code)
    {
        var item = cmd.Element("Item");
        var serverUri = Loc(item, "Target") ?? "";
        var clientUri = Loc(item, "Source") ?? serverUri;
        var anchor = item?.Element("Meta")?.Element("Anchor");
        var last = anchor?.Element("Last")?.Value.Trim();
        var next = anchor?.Element("Next")?.Value.Trim();

        var dbs = await ResolveAsync(serverUri, clientUri);
        if (dbs == null)
        {
            _out.Add(Status("Alert", cmdId, serverUri, clientUri, 404));
            return;
        }

        var normServer = Norm(serverUri).ToLowerInvariant();
        SyncMlAnchor state;
        using (await _gate.EnterAsync())
        {
            state = await _db.SyncMlAnchors.FirstOrDefaultAsync(a => a.DeviceId == _session.DeviceRowId && a.ClientDatabase == clientUri && a.ServerDatabase == normServer)
                    ?? new SyncMlAnchor { DeviceId = _session.DeviceRowId!.Value, ClientDatabase = clientUri, ServerDatabase = normServer };
            if (state.Id == 0)
            {
                _db.SyncMlAnchors.Add(state);
                await _db.SaveChangesAsync();
            }
        }

        var effective = code;
        var status = 200;
        var anchorsMatch = state.LastClientAnchor != null && state.LastClientAnchor == last && state.ServerSyncToken > 0;
        if (code is 200 or 202 or 204 && !anchorsMatch)
        {
            effective = code switch { 200 => 201, 202 => 203, _ => 205 };
            status = 508;
        }
        if (!dbs.CanWrite && effective is 200 or 201 or 202 or 203) effective = effective is 200 or 202 ? 204 : 205;

        dbs.AlertCode = effective;
        dbs.ClientNext = next;
        dbs.ServerNext = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        dbs.AnchorId = state.Id;
        dbs.BaseToken = state.ServerSyncToken;

        foreach (var m in await _db.SyncMlMappings.Where(m => m.AnchorId == state.Id).ToListAsync())
            dbs.LuidToUri[m.Luid] = m.ServerUri;

        if (effective is 203 or 205)
        {
            using (await _gate.EnterAsync())
                await _db.SyncMlMappings.Where(m => m.AnchorId == state.Id).ExecuteDeleteAsync();
            dbs.LuidToUri.Clear();
        }
        if (effective == 201) dbs.SlowIndex = await BuildSlowIndexAsync(dbs);

        _session.Databases.RemoveAll(d => d.ClientUri == clientUri);
        _session.Databases.Add(dbs);

        _out.Add(Status("Alert", cmdId, serverUri, clientUri, status,
            item: next == null ? null : E("Item", E("Data", E("Anchor", E("Next", next))))));
        _out.Add(E("Alert", E("CmdID", NextCmdId()), E("Data", effective.ToString()),
            E("Item",
                E("Target", E("LocURI", clientUri)),
                E("Source", E("LocURI", serverUri)),
                E("Meta", E("Anchor",
                    state.LastServerAnchor == null ? null : E("Last", state.LastServerAnchor),
                    E("Next", dbs.ServerNext))))));
    }

    // ------------------------------------------------------------------ Client changes

    private async Task ProcessSyncAsync(XElement sync, string cmdId)
    {
        var serverUri = Loc(sync, "Target") ?? "";
        var clientUri = Loc(sync, "Source") ?? "";
        var dbs = _session.Databases.FirstOrDefault(d => d.ClientUri == clientUri)
                  ?? _session.Databases.FirstOrDefault(d => Norm(d.ServerUri).Equals(Norm(serverUri), StringComparison.OrdinalIgnoreCase));
        if (dbs == null)
        {
            _out.Add(Status("Sync", cmdId, serverUri, clientUri, 404));
            return;
        }
        _out.Add(Status("Sync", cmdId, serverUri, clientUri, 200));
        var syncType = sync.Element("Meta")?.Element("Type")?.Value;

        async Task Process(XElement container, string? inheritedType)
        {
            foreach (var op in container.Elements())
            {
                var name = op.Name.LocalName;
                var opId = op.Element("CmdID")?.Value.Trim() ?? "0";
                if (name is "Atomic" or "Sequence")
                {
                    _out.Add(Status(name, opId, null, null, 200));
                    await Process(op, inheritedType);
                    continue;
                }
                if (name is not ("Add" or "Replace" or "Delete" or "Copy" or "Move")) continue;
                var opType = op.Element("Meta")?.Element("Type")?.Value ?? inheritedType;
                var opFormat = op.Element("Meta")?.Element("Format")?.Value;
                foreach (var item in op.Elements("Item"))
                {
                    var luid = Loc(item, "Source") ?? Loc(item, "Target") ?? "";
                    int code;
                    if (name is "Copy" or "Move") code = 406;
                    else if (!dbs.CanWrite || dbs.AlertCode is 204 or 205) code = 200; // server-to-client only: ignore
                    else
                    {
                        var type = item.Element("Meta")?.Element("Type")?.Value ?? opType;
                        var format = item.Element("Meta")?.Element("Format")?.Value ?? opFormat;
                        var data = item.Element("Data")?.Value ?? "";
                        if (string.Equals(format, "b64", StringComparison.OrdinalIgnoreCase))
                        {
                            try { data = Encoding.UTF8.GetString(Convert.FromBase64String(data.Trim())); }
                            catch (FormatException) { data = ""; }
                        }
                        if (type != null) dbs.IncomingType = type.Trim();
                        code = await ApplyAsync(dbs, name, luid, type?.Trim(), data);
                    }
                    _out.Add(Status(name, opId, null, luid, code));
                }
            }
        }

        await Process(sync, syncType);
    }

    private async Task<int> ApplyAsync(DbSession dbs, string op, string luid, string? type, string data)
    {
        using var _ = await _gate.EnterAsync();
        dbs.LuidToUri.TryGetValue(luid, out var mappedUri);
        try
        {
            if (op == "Delete")
            {
                if (mappedUri == null) return 211;
                await DeleteServerItemAsync(dbs, mappedUri);
                await RemoveMappingAsync(dbs, luid);
                dbs.Touched.Add(mappedUri);
                return 200;
            }

            if (string.IsNullOrWhiteSpace(data)) return 400;
            return dbs.IsContacts
                ? await ApplyCardAsync(dbs, luid, mappedUri, type, data)
                : await ApplyCalendarAsync(dbs, luid, mappedUri, type, data);
        }
        catch (InvalidObjectException ex)
        {
            _log.LogInformation("SyncML item {Luid} rejected: {Message}", luid, ex.Message);
            return 415;
        }
        catch (QuotaExceededException)
        {
            return 420; // device full (storage quota exceeded)
        }
    }

    private async Task<int> ApplyCardAsync(DbSession dbs, string luid, string? mappedUri, string? type, string data)
    {
        var book = await _db.AddressBooks.FirstAsync(b => b.Id == dbs.CollectionId);
        var text = LegacyFormats.VCardTo30(data);
        var existing = mappedUri == null ? null : await _db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Id && c.Uri == mappedUri);
        var info = CardInfo.Analyze(text, existing?.Uid ?? Guid.NewGuid().ToString());

        if (existing == null && dbs.SlowIndex != null && MatchSlow(dbs, "UID:" + info.Uid, "FN:" + (info.FormattedName ?? "").Trim().ToLowerInvariant()) is { } matchedUri)
            existing = await _db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Id && c.Uri == matchedUri);

        if (existing != null && existing.Uid != info.Uid)
        {
            // Keep the stored UID: legacy clients do not round-trip it.
            text = ReplaceProperty(text, "UID", existing.Uid);
            info = CardInfo.Analyze(text, existing.Uid);
        }
        else if (string.IsNullOrEmpty(info.Root.Value("UID")))
        {
            text = ReplaceProperty(text, "UID", info.Uid);
        }

        var store = Books;
        var uri = existing?.Uri ?? CalendarStore.SafeObjectName(info.Uid) + ".vcf";
        if (existing == null && await _db.Cards.AnyAsync(c => c.AddressBookId == book.Id && c.Uri == uri)) uri = Guid.NewGuid().ToString("N") + ".vcf";
        try { await store.PutAsync(book, uri, text, info, existing); }
        catch (UidConflictException ex)
        {
            existing = await _db.Cards.FirstAsync(c => c.AddressBookId == book.Id && c.Uri == ex.ExistingUri);
            uri = existing.Uri;
            await store.PutAsync(book, uri, text, info, existing);
        }
        dbs.Touched.Add(uri);
        await SetMappingAsync(dbs, luid, uri);
        return existing == null ? 201 : 200;
    }

    private async Task<int> ApplyCalendarAsync(DbSession dbs, string luid, string? mappedUri, string? type, string data)
    {
        var cal = await _db.Calendars.FirstAsync(c => c.Id == dbs.CollectionId);
        var existing = mappedUri == null ? null : await _db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Id && o.Uri == mappedUri);

        var t = type?.ToLowerInvariant() ?? "";
        var isNote = dbs.Kind == DbKind.Notes && !t.Contains("calendar") && !data.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase);
        string text = isNote
            ? LegacyFormats.NoteToJournal(data, t, existing?.Data)
            : LegacyFormats.VCalendarTo20(data, existing?.Data, out _);
        var info = CalendarObjectInfo.Analyze(text);
        if (!dbs.Components.Contains(info.ComponentType) || !cal.ComponentList.Contains(info.ComponentType)) return 415;

        if (existing == null && dbs.SlowIndex != null && MatchSlow(dbs, "UID:" + info.Uid, ContentKey(info.Root)) is { } matchedUri)
            existing = await _db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Id && o.Uri == matchedUri);
        if (existing != null && existing.Uid != info.Uid && !string.IsNullOrEmpty(existing.Uid))
        {
            text = ReplaceUid(text, existing.Uid);
            info = CalendarObjectInfo.Analyze(text);
        }

        var store = Calendars;
        var uri = existing?.Uri ?? CalendarStore.SafeObjectName(info.Uid) + ".ics";
        if (existing == null && await _db.CalendarObjects.AnyAsync(o => o.CalendarId == cal.Id && o.Uri == uri)) uri = Guid.NewGuid().ToString("N") + ".ics";
        try { await store.PutAsync(cal, uri, text, info, existing); }
        catch (UidConflictException ex)
        {
            existing = await _db.CalendarObjects.FirstAsync(o => o.CalendarId == cal.Id && o.Uri == ex.ExistingUri);
            uri = existing.Uri;
            await store.PutAsync(cal, uri, text, info, existing);
        }
        dbs.Touched.Add(uri);
        await SetMappingAsync(dbs, luid, uri);
        return existing == null ? 201 : 200;
    }

    private static string ReplaceProperty(string text, string name, string value)
    {
        var root = ContentReader.Parse(text)[0];
        root.Properties.RemoveAll(p => p.Name == name);
        root.Properties.Insert(Math.Min(1, root.Properties.Count), new ContentLine(name, value));
        return ContentWriter.Write(root);
    }

    private static string ReplaceUid(string text, string uid)
    {
        var root = CalendarObjectInfo.ParseCalendar(text);
        foreach (var c in root.Children.Where(c => CalendarObjectInfo.ObjectComponents.Contains(c.Name)))
        {
            c.Properties.RemoveAll(p => p.Name == "UID");
            c.Properties.Insert(0, new ContentLine("UID", uid));
        }
        return ContentWriter.Write(root);
    }

    private static string ContentKey(Component vcalendar)
    {
        var c = vcalendar.Children.FirstOrDefault(x => x.Name is "VEVENT" or "VTODO" or "VJOURNAL");
        if (c == null) return "";
        var summary = c.Property("SUMMARY")?.TextValue.Trim().ToLowerInvariant() ?? "";
        DateTime? start = ICalTime.TryParse(c.Property("DTSTART"), out var s) ? new CalendarAnalyzer(vcalendar).ToUtc(s) : null;
        return $"{c.Name}:{summary}|{start:yyyyMMddHHmm}";
    }

    private static string? MatchSlow(DbSession dbs, string uidKey, string contentKey)
    {
        foreach (var key in new[] { uidKey, contentKey })
        {
            if (key.Length <= 4 || !dbs.SlowIndex!.TryGetValue(key, out var uri) || dbs.Touched.Contains(uri)) continue;
            return uri;
        }
        return null;
    }

    private async Task<Dictionary<string, string>> BuildSlowIndexAsync(DbSession dbs)
    {
        var index = new Dictionary<string, string>();
        if (dbs.IsContacts)
        {
            foreach (var c in await _db.Cards.AsNoTracking().Where(c => c.AddressBookId == dbs.CollectionId).Select(c => new { c.Uri, c.Uid, c.FormattedName }).ToListAsync())
            {
                index.TryAdd("UID:" + c.Uid, c.Uri);
                if (!string.IsNullOrWhiteSpace(c.FormattedName)) index.TryAdd("FN:" + c.FormattedName.Trim().ToLowerInvariant(), c.Uri);
            }
        }
        else
        {
            var comps = dbs.Components;
            foreach (var o in await _db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == dbs.CollectionId && comps.Contains(o.ComponentType)).ToListAsync())
            {
                index.TryAdd("UID:" + o.Uid, o.Uri);
                try { index.TryAdd(ContentKey(CalendarObjectInfo.ParseCalendar(o.Data)), o.Uri); }
                catch (InvalidObjectException) { }
            }
        }
        return index;
    }

    private async Task DeleteServerItemAsync(DbSession dbs, string uri)
    {
        if (dbs.IsContacts)
        {
            var book = await _db.AddressBooks.FirstAsync(b => b.Id == dbs.CollectionId);
            var card = await _db.Cards.FirstOrDefaultAsync(c => c.AddressBookId == book.Id && c.Uri == uri);
            if (card != null) await Books.DeleteCardAsync(book, card);
        }
        else
        {
            var cal = await _db.Calendars.FirstAsync(c => c.Id == dbs.CollectionId);
            var obj = await _db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == cal.Id && o.Uri == uri);
            if (obj != null) await Calendars.DeleteObjectAsync(cal, obj);
        }
    }

    private async Task SetMappingAsync(DbSession dbs, string luid, string uri)
    {
        if (string.IsNullOrEmpty(luid)) return;
        if (dbs.LuidToUri.TryGetValue(luid, out var current) && current == uri) return;
        dbs.LuidToUri[luid] = uri;
        var row = await _db.SyncMlMappings.FirstOrDefaultAsync(m => m.AnchorId == dbs.AnchorId && m.Luid == luid);
        if (row == null) _db.SyncMlMappings.Add(new SyncMlMapping { AnchorId = dbs.AnchorId, Luid = luid, ServerUri = uri });
        else row.ServerUri = uri;
        await _db.SaveChangesAsync();
    }

    private async Task RemoveMappingAsync(DbSession dbs, string luid)
    {
        dbs.LuidToUri.Remove(luid);
        await _db.SyncMlMappings.Where(m => m.AnchorId == dbs.AnchorId && m.Luid == luid).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------------ Map

    private async Task ProcessMapAsync(XElement map, string cmdId)
    {
        var clientUri = Loc(map, "Source") ?? "";
        var serverUri = Loc(map, "Target") ?? "";
        var dbs = _session.Databases.FirstOrDefault(d => d.ClientUri == clientUri)
                  ?? _session.Databases.FirstOrDefault(d => Norm(d.ServerUri).Equals(Norm(serverUri), StringComparison.OrdinalIgnoreCase));
        if (dbs == null)
        {
            _out.Add(Status("Map", cmdId, serverUri, clientUri, 404));
            return;
        }
        using (await _gate.EnterAsync())
        {
            foreach (var mi in map.Elements("MapItem"))
            {
                var uri = Loc(mi, "Target");
                var luid = Loc(mi, "Source");
                if (!string.IsNullOrEmpty(uri) && !string.IsNullOrEmpty(luid)) await SetMappingAsync(dbs, luid, uri);
            }
        }
        _out.Add(Status("Map", cmdId, serverUri, clientUri, 200));
    }

    // ------------------------------------------------------------------ Server changes

    private sealed record ServerItem(string Uri, string Data, string ComponentType);

    private async Task<List<ServerItem>> LoadItemsAsync(DbSession dbs, IReadOnlyCollection<string>? uris)
    {
        if (dbs.IsContacts)
        {
            var q = _db.Cards.AsNoTracking().Where(c => c.AddressBookId == dbs.CollectionId);
            if (uris != null) q = q.Where(c => uris.Contains(c.Uri));
            return (await q.Select(c => new { c.Uri, c.Data }).ToListAsync()).Select(c => new ServerItem(c.Uri, c.Data, "VCARD")).ToList();
        }
        var comps = dbs.Components;
        var oq = _db.CalendarObjects.AsNoTracking().Where(o => o.CalendarId == dbs.CollectionId && comps.Contains(o.ComponentType));
        if (uris != null) oq = oq.Where(o => uris.Contains(o.Uri));
        return (await oq.Select(o => new { o.Uri, o.Data, o.ComponentType }).ToListAsync())
            .Select(o => new ServerItem(o.Uri, o.Data, o.ComponentType)).ToList();
    }

    private async Task QueueServerChangesAsync(DbSession dbs)
    {
        dbs.ServerChangesQueued = true;
        var commands = new List<XElement>();

        using (await _gate.EnterAsync())
        {
            if (dbs.AlertCode == 203)
            {
                // Refresh from client: the client's data replaces the server's.
                var stale = (await LoadItemsAsync(dbs, null)).Where(i => !dbs.Touched.Contains(i.Uri)).Select(i => i.Uri).ToList();
                foreach (var uri in stale) await DeleteServerItemAsync(dbs, uri);
            }
            else if (dbs.AlertCode is 201 or 205)
            {
                if (dbs.AlertCode == 201)
                {
                    var touchedLuids = dbs.LuidToUri.Where(kv => !dbs.Touched.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                    foreach (var luid in touchedLuids) await RemoveMappingAsync(dbs, luid);
                }
                foreach (var item in (await LoadItemsAsync(dbs, null)).Where(i => !dbs.Touched.Contains(i.Uri)))
                    if (Outgoing("Add", dbs, item, null) is { } cmd) commands.Add(cmd);
            }
            else if (dbs.AlertCode is 200 or 204)
            {
                var changed = dbs.IsContacts
                    ? await _db.CardChanges.Where(c => c.AddressBookId == dbs.CollectionId && c.SyncToken > dbs.BaseToken).Select(c => c.Uri).Distinct().ToListAsync()
                    : await _db.CalendarChanges.Where(c => c.CalendarId == dbs.CollectionId && c.SyncToken > dbs.BaseToken).Select(c => c.Uri).Distinct().ToListAsync();
                changed = changed.Where(u => !dbs.Touched.Contains(u)).ToList();
                var current = (await LoadItemsAsync(dbs, changed)).ToDictionary(i => i.Uri);
                var uriToLuid = dbs.LuidToUri.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

                foreach (var uri in changed)
                {
                    uriToLuid.TryGetValue(uri, out var luid);
                    if (current.TryGetValue(uri, out var item))
                    {
                        if (Outgoing(luid == null ? "Add" : "Replace", dbs, item, luid) is { } cmd) commands.Add(cmd);
                    }
                    else if (luid != null)
                    {
                        commands.Add(E("Delete", E("Item", E("Target", E("LocURI", luid)))));
                        await RemoveMappingAsync(dbs, luid);
                    }
                }
            }

            // Commit anchors: the next normal sync continues from here.
            var state = await _db.SyncMlAnchors.FirstAsync(a => a.Id == dbs.AnchorId);
            state.LastClientAnchor = dbs.ClientNext;
            state.LastServerAnchor = dbs.ServerNext;
            state.ServerSyncToken = await CurrentTokenAsync(dbs);
            state.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        if (dbs.AlertCode is 202 or 203) commands.Clear();
        if (commands.Count == 0) _session.Outbox.Add((dbs, null));
        foreach (var c in commands) _session.Outbox.Add((dbs, c));
    }

    private string PreferredType(DbSession dbs)
    {
        if (_session.ClientPreferred.TryGetValue(Norm(dbs.ClientUri), out var pref)) return pref;
        if (!string.IsNullOrEmpty(dbs.IncomingType)) return dbs.IncomingType;
        return dbs.Kind switch
        {
            DbKind.Contacts => "text/x-vcard",
            DbKind.Notes => "text/plain",
            _ => "text/x-vcalendar",
        };
    }

    private XElement? Outgoing(string op, DbSession dbs, ServerItem item, string? luid)
    {
        var type = PreferredType(dbs);
        string data;
        try
        {
            var t = type.ToLowerInvariant();
            data = dbs.Kind switch
            {
                DbKind.Contacts => t.Contains("x-vcard") ? LegacyFormats.VCardTo21(item.Data) : item.Data,
                DbKind.Notes when t.Contains("vnote") => LegacyFormats.JournalToVNote(item.Data),
                DbKind.Notes when !t.Contains("calendar") => LegacyFormats.JournalToText(item.Data),
                _ => t.Contains("x-vcalendar") ? LegacyFormats.VCalendarTo10(item.Data) : item.Data,
            };
        }
        catch (Exception ex) when (ex is InvalidObjectException or ContentParseException or InvalidOperationException)
        {
            _log.LogInformation("Skipping {Uri} for SyncML: {Message}", item.Uri, ex.Message);
            return null;
        }

        var target = luid == null ? null : E("Target", E("LocURI", luid));
        return E(op, E("Meta", E("Type", type)),
            E("Item", target, E("Source", E("LocURI", item.Uri)), E("Data", data)));
    }

    /// <summary>Moves queued server commands into this message. Returns true when the outbox is empty.</summary>
    private bool FlushOutbox()
    {
        var used = _out.Sum(e => e.ToString(SaveOptions.DisableFormatting).Length) + 600;
        var sent = new HashSet<DbSession>();
        XElement? currentSync = null;
        DbSession? currentDb = null;
        var count = 0;

        while (_session.Outbox.Count > 0)
        {
            var (db, cmd) = _session.Outbox[0];
            var size = cmd?.ToString(SaveOptions.DisableFormatting).Length ?? 0;
            if (count > 0 && used + size > _budget) break;
            _session.Outbox.RemoveAt(0);

            if (currentDb != db)
            {
                var total = _session.Outbox.Count(o => o.Db == db && o.Command != null) + (cmd != null ? 1 : 0);
                currentSync = E("Sync", E("CmdID", NextCmdId()),
                    E("Target", E("LocURI", db.ClientUri)),
                    E("Source", E("LocURI", db.ServerUri)),
                    _session.Version != "1.0" && !sent.Contains(db) ? E("NumberOfChanges", total.ToString()) : null);
                _out.Add(currentSync);
                currentDb = db;
                sent.Add(db);
            }
            if (cmd != null)
            {
                cmd.AddFirst(E("CmdID", NextCmdId()));
                currentSync!.Add(cmd);
                used += size;
                count++;
            }
        }
        return _session.Outbox.Count == 0;
    }
}
