using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Calendar = Nullify.CalDav.Data.Calendar;
using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Services;

/// <summary>
/// Server-side implicit scheduling (RFC 6638 "calendar-auto-schedule"): invitations, updates, cancellations and
/// replies are delivered directly to local users' calendars (and scheduling inboxes) and sent as iMIP email
/// (RFC 6047) to external participants.
/// </summary>
public sealed class SchedulingService(AppDbContext db, CalendarStore store, SettingsService settings, MailQueue mail, ILogger<SchedulingService> log)
{
    private static readonly string[] ScheduledComponents = ["VEVENT", "VTODO"];

    public static string? Email(string? calAddress)
    {
        if (string.IsNullOrWhiteSpace(calAddress)) return null;
        var v = calAddress.Trim();
        if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) v = v[7..];
        return v.Contains('@') ? v.ToLowerInvariant() : null;
    }

    private static bool ClientScheduled(ContentLine p) =>
        p.GetParameter("SCHEDULE-AGENT")?.ToUpperInvariant() is "CLIENT" or "NONE";

    private static List<Component> Scheduled(Component root) => root.Children.Where(c => ScheduledComponents.Contains(c.Name)).ToList();

    private static Component? TryParse(string? data)
    {
        if (data == null) return null;
        try { return CalendarObjectInfo.ParseCalendar(data); }
        catch (InvalidObjectException) { return null; }
    }

    private static Component Clone(Component root) => CalendarObjectInfo.ParseCalendar(ContentWriter.Write(root));

    private static void SetParam(ContentLine line, string name, string? value)
    {
        line.Parameters.RemoveAll(p => p.Name == name);
        if (value != null) line.Parameters.Add(new ContentParameter(name, [value]));
    }

    private static void SetProperty(Component c, string name, string value)
    {
        c.Properties.RemoveAll(p => p.Name == name);
        c.Properties.Add(new ContentLine(name, value));
    }

    private static void SetMethod(Component root, string? method)
    {
        root.Properties.RemoveAll(p => p.Name == "METHOD");
        if (method != null) root.Properties.Insert(Math.Min(2, root.Properties.Count), new ContentLine("METHOD", method));
    }

    private static HashSet<string> AttendeeEmails(IEnumerable<Component> comps, string organizer) =>
        comps.SelectMany(c => c.PropertiesNamed("ATTENDEE"))
            .Where(a => !ClientScheduled(a))
            .Select(a => Email(a.Value))
            .Where(e => e != null && e != organizer)
            .Select(e => e!)
            .ToHashSet();

    private static ContentLine? AttendeeLine(Component c, string email) =>
        c.PropertiesNamed("ATTENDEE").FirstOrDefault(a => Email(a.Value) == email);

    private static Component? Master(IEnumerable<Component> comps) =>
        comps.FirstOrDefault(c => c.Property("RECURRENCE-ID") == null) ?? comps.FirstOrDefault();

    /// <summary>Fingerprint of everything an attendee would need to be re-invited for.</summary>
    private static string Signature(Component root)
    {
        var sb = new StringBuilder();
        foreach (var c in Scheduled(root))
        {
            sb.Append(c.Name).Append('\n');
            foreach (var p in c.Properties.Where(p => p.Name is not ("DTSTAMP" or "LAST-MODIFIED" or "SEQUENCE" or "CREATED") && !p.Name.StartsWith("X-")))
            {
                var copy = new ContentLine(p.Name, p.Value);
                foreach (var param in p.Parameters.Where(x => x.Name is not ("PARTSTAT" or "RSVP" or "SCHEDULE-STATUS" or "SCHEDULE-FORCE-SEND")))
                    copy.Parameters.Add(param);
                sb.Append(ContentWriter.Format(copy)).Append('\n');
            }
        }
        return sb.ToString();
    }

    private async Task<User?> LocalUserAsync(string email, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsEnabled, ct);

    // ------------------------------------------------------------------ Entry points

    /// <summary>
    /// Processes a client PUT of a calendar object owned by <paramref name="owner"/>. Returns the data to store,
    /// which may carry updated SCHEDULE-STATUS parameters.
    /// </summary>
    public async Task<string> OnPutAsync(Calendar cal, User owner, string? oldData, string newData, CancellationToken ct)
    {
        if (!settings.Current.SchedulingEnabled || cal.Kind != CalendarKind.Normal || string.IsNullOrEmpty(owner.Email)) return newData;
        var root = TryParse(newData);
        if (root == null) return newData;
        var comps = Scheduled(root);
        var organizer = comps.Select(c => c.Property("ORGANIZER")).FirstOrDefault(p => p != null);
        if (organizer == null || ClientScheduled(organizer)) return newData;
        var orgEmail = Email(organizer.Value);
        var me = owner.Email.ToLowerInvariant();
        var oldRoot = TryParse(oldData);

        try
        {
            if (orgEmail == me) return await OrganizerPutAsync(root, oldRoot, owner, me, ct) ?? newData;
            if (comps.Any(c => AttendeeLine(c, me) != null)) return await AttendeePutAsync(root, oldRoot, owner, me, orgEmail, ct) ?? newData;
        }
        catch (Exception ex) when (ex is InvalidObjectException or ContentParseException)
        {
            log.LogWarning("Scheduling skipped: {Message}", ex.Message);
        }
        return newData;
    }

    /// <summary>Processes the deletion of a calendar object: cancels (organizer) or declines (attendee).</summary>
    public async Task OnDeleteAsync(Calendar cal, User owner, string oldData, CancellationToken ct)
    {
        if (!settings.Current.SchedulingEnabled || cal.Kind != CalendarKind.Normal || string.IsNullOrEmpty(owner.Email)) return;
        var root = TryParse(oldData);
        if (root == null) return;
        var comps = Scheduled(root);
        var organizer = comps.Select(c => c.Property("ORGANIZER")).FirstOrDefault(p => p != null);
        if (organizer == null || ClientScheduled(organizer)) return;
        var orgEmail = Email(organizer.Value);
        var me = owner.Email.ToLowerInvariant();

        if (orgEmail == me)
        {
            // Events that have already ended are not cancelled for the attendees.
            var cancel = Clone(root);
            foreach (var c in Scheduled(cancel))
            {
                SetProperty(c, "STATUS", "CANCELLED");
                SetProperty(c, "SEQUENCE", (int.TryParse(c.Value("SEQUENCE"), out var seq) ? seq + 1 : 1).ToString(CultureInfo.InvariantCulture));
            }
            foreach (var attendee in AttendeeEmails(comps, me))
                await SendAsync(attendee, cancel, "CANCEL", owner, isUpdate: false, ct);
        }
        else if (comps.Any(c => AttendeeLine(c, me) != null) && orgEmail != null)
        {
            var decline = Clone(root);
            foreach (var c in Scheduled(decline))
                if (AttendeeLine(c, me) is { } a) SetParam(a, "PARTSTAT", "DECLINED");
            await SendReplyAsync(decline, owner, me, orgEmail, ct);
        }
    }

    // ------------------------------------------------------------------ Organizer

    private async Task<string?> OrganizerPutAsync(Component root, Component? oldRoot, User owner, string me, CancellationToken ct)
    {
        var comps = Scheduled(root);
        var attendees = AttendeeEmails(comps, me);
        var previous = oldRoot == null ? [] : AttendeeEmails(Scheduled(oldRoot), me);
        var significant = oldRoot == null || Signature(oldRoot) != Signature(root);
        var targets = significant ? attendees : attendees.Except(previous).ToHashSet();
        var forced = comps.SelectMany(c => c.PropertiesNamed("ATTENDEE"))
            .Where(a => a.GetParameter("SCHEDULE-FORCE-SEND") != null).Select(a => Email(a.Value)).Where(e => e != null);
        targets.UnionWith(forced.Where(e => attendees.Contains(e!))!);

        var statuses = new Dictionary<string, string>();
        foreach (var attendee in targets)
            statuses[attendee] = await SendAsync(attendee, root, "REQUEST", owner, isUpdate: oldRoot != null && previous.Contains(attendee), ct);

        if (oldRoot != null)
        {
            foreach (var removed in previous.Except(attendees))
            {
                var cancel = Clone(oldRoot);
                foreach (var c in Scheduled(cancel)) SetProperty(c, "STATUS", "CANCELLED");
                await SendAsync(removed, cancel, "CANCEL", owner, isUpdate: false, ct);
            }
        }
        if (statuses.Count == 0) return null;

        foreach (var c in comps)
        foreach (var a in c.PropertiesNamed("ATTENDEE"))
        {
            if (Email(a.Value) is { } e && statuses.TryGetValue(e, out var status)) SetParam(a, "SCHEDULE-STATUS", status);
            a.Parameters.RemoveAll(p => p.Name == "SCHEDULE-FORCE-SEND");
        }
        return ContentWriter.Write(root);
    }

    /// <summary>Delivers a REQUEST or CANCEL to one attendee. Returns the RFC 6638 SCHEDULE-STATUS.</summary>
    private async Task<string> SendAsync(string attendee, Component source, string method, User organizer, bool isUpdate, CancellationToken ct)
    {
        var local = await LocalUserAsync(attendee, ct);
        if (local != null && local.Id != organizer.Id)
            return await DeliverToLocalAsync(local, source, method, ct);
        return SendEmail(attendee, source, method, organizer, isUpdate);
    }

    private string SendEmail(string to, Component source, string method, User sender, bool isUpdate)
    {
        var s = settings.Current;
        if (!s.SendInvitationEmails || !s.SmtpConfigured) return "5.1";
        var message = Clone(source);
        SetMethod(message, method);
        StripScheduleParams(message);
        if (method == "REQUEST") foreach (var c in Scheduled(message)) c.Children.RemoveAll(x => x.Name == "VALARM");
        mail.Enqueue(Imip.Build(method, message, sender, to, s.InvitationFrom!, isUpdate));
        return "1.1";
    }

    private static void StripScheduleParams(Component root)
    {
        foreach (var c in Scheduled(root))
        foreach (var p in c.Properties.Where(p => p.Name is "ATTENDEE" or "ORGANIZER"))
            p.Parameters.RemoveAll(x => x.Name is "SCHEDULE-STATUS" or "SCHEDULE-FORCE-SEND");
    }

    private async Task<string> DeliverToLocalAsync(User user, Component source, string method, CancellationToken ct)
    {
        var copy = Clone(source);
        SetMethod(copy, null);
        StripScheduleParams(copy);
        var uid = Scheduled(copy).Select(c => c.Value("UID")).FirstOrDefault(u => !string.IsNullOrEmpty(u));
        if (uid == null) return "5.1";
        try
        {
            var existing = await db.CalendarObjects.Include(o => o.Calendar)
                .FirstOrDefaultAsync(o => o.Calendar.OwnerId == user.Id && o.Calendar.Kind == CalendarKind.Normal && o.Uid == uid, ct);

            if (method == "CANCEL")
            {
                if (existing != null)
                {
                    var theirs = CalendarObjectInfo.ParseCalendar(existing.Data);
                    foreach (var c in Scheduled(theirs)) SetProperty(c, "STATUS", "CANCELLED");
                    var data = ContentWriter.Write(theirs);
                    await store.PutAsync(existing.Calendar, existing.Uri, data, CalendarObjectInfo.Analyze(data), existing, ct: ct);
                }
            }
            else
            {
                // The attendee keeps their own alarms; the organizer's are not imposed on them.
                var keepAlarms = existing == null ? null : Scheduled(CalendarObjectInfo.ParseCalendar(existing.Data))
                    .ToDictionary(c => c.Value("RECURRENCE-ID") ?? "", c => c.ChildrenNamed("VALARM").ToList());
                foreach (var c in Scheduled(copy))
                {
                    c.Children.RemoveAll(x => x.Name == "VALARM");
                    if (keepAlarms != null && keepAlarms.TryGetValue(c.Value("RECURRENCE-ID") ?? "", out var alarms)) c.Children.AddRange(alarms);
                }
                var data = ContentWriter.Write(copy);
                var info = CalendarObjectInfo.Analyze(data);
                if (existing != null)
                {
                    await store.PutAsync(existing.Calendar, existing.Uri, data, info, existing, ct: ct);
                }
                else
                {
                    var target = await db.Calendars
                        .Where(c => c.OwnerId == user.Id && c.Kind == CalendarKind.Normal && c.Components.Contains(info.ComponentType))
                        .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
                    if (target == null) return "5.3";
                    await store.PutAsync(target, Guid.NewGuid().ToString("N") + ".ics", data, info, null, ct: ct);
                }
            }

            await PutInInboxAsync(user, source, method, ct);
            return "1.2";
        }
        catch (Exception ex) when (ex is QuotaExceededException or UidConflictException or InvalidObjectException)
        {
            log.LogInformation("Delivery to {User} failed: {Message}", user.Username, ex.Message);
            return "5.1";
        }
    }

    private async Task PutInInboxAsync(User user, Component source, string method, CancellationToken ct)
    {
        var inbox = await db.Calendars.FirstOrDefaultAsync(c => c.OwnerId == user.Id && c.Kind == CalendarKind.Inbox, ct);
        if (inbox == null) return;
        var message = Clone(source);
        SetMethod(message, method);
        StripScheduleParams(message);
        var data = ContentWriter.Write(message);
        try { await store.PutAsync(inbox, Guid.NewGuid().ToString("N") + ".ics", data, CalendarObjectInfo.Analyze(data), null, ct: ct); }
        catch (QuotaExceededException) { }
    }

    // ------------------------------------------------------------------ Attendee

    private static string PartStat(Component? c, string me) =>
        c == null ? "NEEDS-ACTION" : (AttendeeLine(c, me)?.GetParameter("PARTSTAT") ?? "NEEDS-ACTION").ToUpperInvariant();

    private async Task<string?> AttendeePutAsync(Component root, Component? oldRoot, User owner, string me, string? orgEmail, CancellationToken ct)
    {
        if (orgEmail == null) return null;
        var comps = Scheduled(root);
        var oldComps = oldRoot == null ? [] : Scheduled(oldRoot);
        var changed = comps.Any(c =>
        {
            var rid = c.Value("RECURRENCE-ID");
            var old = oldComps.FirstOrDefault(o => o.Value("RECURRENCE-ID") == rid) ?? Master(oldComps);
            var now = PartStat(c, me);
            return oldRoot == null ? now != "NEEDS-ACTION" : now != PartStat(old, me);
        });
        if (!changed) return null;

        var status = await SendReplyAsync(root, owner, me, orgEmail, ct);
        foreach (var c in comps)
            if (c.Property("ORGANIZER") is { } org) SetParam(org, "SCHEDULE-STATUS", status);
        return ContentWriter.Write(root);
    }

    private async Task<string> SendReplyAsync(Component root, User owner, string me, string orgEmail, CancellationToken ct)
    {
        // A REPLY carries only the replying attendee.
        var reply = Clone(root);
        foreach (var c in Scheduled(reply))
        {
            c.Properties.RemoveAll(p => p.Name == "ATTENDEE" && Email(p.Value) != me);
            c.Children.RemoveAll(x => x.Name == "VALARM");
            SetProperty(c, "DTSTAMP", ICalTime.FormatUtc(DateTime.UtcNow));
        }
        StripScheduleParams(reply);

        var organizer = await LocalUserAsync(orgEmail, ct);
        if (organizer == null || organizer.Id == owner.Id) return SendEmail(orgEmail, reply, "REPLY", owner, isUpdate: false);

        var uid = Scheduled(reply).Select(c => c.Value("UID")).FirstOrDefault();
        var original = await db.CalendarObjects.Include(o => o.Calendar)
            .FirstOrDefaultAsync(o => o.Calendar.OwnerId == organizer.Id && o.Calendar.Kind == CalendarKind.Normal && o.Uid == uid, ct);
        if (original != null)
        {
            var theirs = CalendarObjectInfo.ParseCalendar(original.Data);
            var theirComps = Scheduled(theirs);
            foreach (var rc in Scheduled(reply))
            {
                var rid = rc.Value("RECURRENCE-ID");
                var target = theirComps.FirstOrDefault(t => t.Value("RECURRENCE-ID") == rid);
                if (target == null) continue;
                var line = AttendeeLine(target, me);
                var replied = AttendeeLine(rc, me);
                if (line == null || replied == null) continue;
                SetParam(line, "PARTSTAT", replied.GetParameter("PARTSTAT") ?? "NEEDS-ACTION");
                SetParam(line, "SCHEDULE-STATUS", "2.0");
                line.Parameters.RemoveAll(p => p.Name == "RSVP");
            }
            var data = ContentWriter.Write(theirs);
            try { await store.PutAsync(original.Calendar, original.Uri, data, CalendarObjectInfo.Analyze(data), original, preserveScheduleTag: true, ct: ct); }
            catch (QuotaExceededException) { return "5.1"; }
        }
        await PutInInboxAsync(organizer, reply, "REPLY", ct);
        return "1.2";
    }

    // ------------------------------------------------------------------ Free/busy (outbox POST)

    /// <summary>Busy periods of a local user across their own calendars.</summary>
    public async Task<List<FreeBusy.Period>?> FreeBusyForAsync(string email, DateTime start, DateTime end, CancellationToken ct)
    {
        var user = await LocalUserAsync(email, ct);
        if (user == null) return null;
        var s = CalendarObjectInfo.ToUnix(start);
        var e = CalendarObjectInfo.ToUnix(end);
        var datas = await db.CalendarObjects.AsNoTracking()
            .Where(o => o.Calendar.OwnerId == user.Id && o.Calendar.Kind == CalendarKind.Normal && o.ComponentType == "VEVENT" &&
                        (o.FirstOccurrence == null || o.FirstOccurrence < e) && (o.LastOccurrence == null || o.LastOccurrence >= s))
            .Select(o => o.Data).ToListAsync(ct);
        return FreeBusy.Compute(datas, start, end);
    }
}

/// <summary>Builds iMIP (RFC 6047) messages.</summary>
public static class Imip
{
    public static OutgoingMail Build(string method, Component message, User sender, string to, string from, bool isUpdate)
    {
        var comp = message.Children.FirstOrDefault(c => c.Name is "VEVENT" or "VTODO");
        var summary = comp?.Property("SUMMARY")?.TextValue ?? "(no title)";
        var name = string.IsNullOrEmpty(sender.DisplayName) ? sender.Username : sender.DisplayName;
        var partstat = comp?.PropertiesNamed("ATTENDEE").FirstOrDefault()?.GetParameter("PARTSTAT")?.ToUpperInvariant();
        var subject = method switch
        {
            "CANCEL" => "Cancelled: " + summary,
            "REPLY" => (partstat switch { "ACCEPTED" => "Accepted", "DECLINED" => "Declined", "TENTATIVE" => "Tentative", _ => "Reply" }) + ": " + summary,
            _ => (isUpdate ? "Updated invitation: " : "Invitation: ") + summary,
        };

        var when = "";
        if (comp != null && ICalTime.TryParse(comp.Property("DTSTART"), out var start))
        {
            var utc = new CalendarAnalyzer(message).ToUtc(start);
            when = start.IsDate ? start.Value.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)
                : utc.ToString("dddd d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }
        var text = new StringBuilder();
        text.AppendLine(method switch
        {
            "CANCEL" => $"{name} has cancelled \"{summary}\".",
            "REPLY" => $"{name} has replied to \"{summary}\" ({partstat?.ToLowerInvariant() ?? "reply"}).",
            _ => $"{name} has invited you to \"{summary}\".",
        });
        if (when.Length > 0) text.AppendLine("When: " + when);
        if (comp?.Property("LOCATION")?.TextValue is { Length: > 0 } loc) text.AppendLine("Where: " + loc);
        if (comp?.Property("DESCRIPTION")?.TextValue is { Length: > 0 } desc) text.AppendLine().AppendLine(desc);

        var ics = ContentWriter.Write(message);
        var b1 = "nullify-" + Guid.NewGuid().ToString("N");
        var b2 = "nullify-" + Guid.NewGuid().ToString("N");
        var host = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : "localhost";

        var sb = new StringBuilder();
        sb.Append($"From: {EncodeWord(name + " via Nullify.CalDav")} <{from}>\r\n");
        if (!string.IsNullOrEmpty(sender.Email)) sb.Append($"Reply-To: {EncodeWord(name)} <{sender.Email}>\r\n");
        sb.Append($"To: <{to}>\r\n");
        sb.Append($"Subject: {EncodeWord(subject)}\r\n");
        sb.Append($"Date: {DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\r\n");
        sb.Append($"Message-ID: <{Guid.NewGuid():N}@{host}>\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{b1}\"\r\n\r\n");
        sb.Append($"--{b1}\r\nContent-Type: multipart/alternative; boundary=\"{b2}\"\r\n\r\n");
        sb.Append($"--{b2}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Base64Lines(text.ToString()));
        sb.Append($"--{b2}\r\nContent-Type: text/calendar; charset=utf-8; method={method}\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Base64Lines(ics));
        sb.Append($"--{b2}--\r\n");
        sb.Append($"--{b1}\r\nContent-Type: application/ics; name=\"invite.ics\"\r\nContent-Disposition: attachment; filename=\"invite.ics\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Base64Lines(ics));
        sb.Append($"--{b1}--\r\n");
        return new OutgoingMail(from, sender.Email, to, subject, sb.ToString());
    }

    private static string EncodeWord(string value) =>
        value.All(c => c is >= ' ' and <= '~' && c != '"') ? value : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static string Base64Lines(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder();
        for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        return sb.ToString();
    }
}
