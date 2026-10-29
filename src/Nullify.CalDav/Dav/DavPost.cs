// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Nullify.CalDav.Data;
using Nullify.CalDav.Formats;
using Nullify.CalDav.Services;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

public sealed partial class DavHandler
{
    private async Task PostAsync()
    {
        var node = await RequireNodeAsync();
        switch (node)
        {
            case OutboxNode outbox:
                await OutboxPostAsync(outbox);
                return;
            case CalendarNode or AddressBookNode:
                var doc = await DavXml.ReadBodyAsync(Request, Ct);
                if (node is CalendarNode calendar && doc?.Root is { } root && (root.Name == D + "share-resource" || root.Name == CS + "share"))
                    await SharePostAsync(calendar, root);
                else
                    await PushRegisterAsync(node, doc);
                return;
            default:
                throw new DavException(405, message: "POST is not supported on this resource");
        }
    }

    // ---------------------------------------------------------------- WebDAV-Push registration

    private async Task PushRegisterAsync(DavNode node, XDocument? doc)
    {
        var settings = _http.RequestServices.GetRequiredService<SettingsService>().Current;
        if (!settings.PushEnabled) throw new DavException(403, message: "Push is disabled on this server");
        if (doc?.Root?.Name != P + "push-register") throw new DavException(415, message: "Expected a push-register request");

        var (type, id) = node switch
        {
            CalendarNode c => (CollectionChange.Calendar, c.Calendar.Id),
            AddressBookNode b => (CollectionChange.AddressBook, b.Book.Id),
            _ => throw new DavException(405),
        };

        PushService.RegisterResult result;
        try
        {
            result = await _http.RequestServices.GetRequiredService<PushService>().RegisterAsync(_db, _user.Id, type, id, doc.Root, Ct);
        }
        catch (FormatException ex)
        {
            throw new DavException(403, P + "invalid-subscription", ex.Message);
        }

        Response.Headers.Expires = result.Expires.ToString("R", CultureInfo.InvariantCulture);
        Response.Headers.Location = Paths.PushRegistration(result.RegistrationId!);
        Response.StatusCode = result.Status;
    }

    // ---------------------------------------------------------------- Scheduling outbox (free/busy, RFC 6638 §5)

    private async Task OutboxPostAsync(OutboxNode outbox)
    {
        var text = await ReadBodyTextAsync(1024 * 1024, C + "max-resource-size");
        Component root;
        try { root = CalendarObjectInfo.ParseCalendar(text); }
        catch (InvalidObjectException ex) { throw new DavException(400, C + "valid-calendar-data", ex.Message); }

        var fb = root.ChildrenNamed("VFREEBUSY").FirstOrDefault();
        if (!string.Equals(root.Value("METHOD")?.Trim(), "REQUEST", StringComparison.OrdinalIgnoreCase) || fb == null)
            throw new DavException(403, C + "valid-scheduling-message", "Only VFREEBUSY requests can be posted; invitations are scheduled automatically");

        var organizer = SchedulingService.Email(fb.Value("ORGANIZER"));
        if (organizer == null || !string.Equals(organizer, outbox.Owner.Email, StringComparison.OrdinalIgnoreCase))
            throw new DavException(403, C + "organizer-allowed", "ORGANIZER must be your own calendar user address");
        if (!ICalTime.TryParse(fb.Property("DTSTART"), out var startValue) || !ICalTime.TryParse(fb.Property("DTEND"), out var endValue))
            throw new DavException(400, C + "valid-calendar-data", "DTSTART and DTEND are required");
        var analyzer = new CalendarAnalyzer(root);
        var start = analyzer.ToUtc(startValue);
        var end = analyzer.ToUtc(endValue);
        if (end <= start || end - start > TimeSpan.FromDays(400)) throw new DavException(400, message: "Invalid free/busy range");

        var scheduling = _http.RequestServices.GetRequiredService<SchedulingService>();
        var response = new XElement(C + "schedule-response");
        foreach (var attendee in fb.PropertiesNamed("ATTENDEE"))
        {
            var email = SchedulingService.Email(attendee.Value);
            var item = new XElement(C + "response", new XElement(C + "recipient", DavXml.Href(attendee.Value.Trim())));
            var periods = email == null ? null : await scheduling.FreeBusyForAsync(email, start, end, Ct);
            if (periods == null)
            {
                item.Add(new XElement(C + "request-status", "3.7;Invalid calendar user"));
            }
            else
            {
                item.Add(new XElement(C + "request-status", "2.0;Success"));
                var extra = new List<string> { "ORGANIZER:mailto:" + organizer, "ATTENDEE:" + attendee.Value.Trim() };
                if (fb.Value("UID") is { } uid) extra.Add("UID:" + uid);
                item.Add(new XElement(C + "calendar-data", FreeBusy.Write(periods, start, end, "REPLY", extra)));
            }
            response.Add(item);
        }
        await DavXml.WriteAsync(Response, 200, response, Ct);
    }
}
