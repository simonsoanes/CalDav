// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using static Nullify.CalDav.Dav.Ns;

namespace Nullify.CalDav.Dav;

/// <summary>
/// Calendar sharing from clients: draft-pot-webdav-resource-sharing (DAV:share-resource) and
/// CalendarServer sharing (CS:share). Shares take effect at once, as they do from the web UI:
/// the calendar appears in the sharee's home without an invitation to accept.
/// </summary>
public sealed partial class DavHandler
{
    /// <summary>A user or group a share request names.</summary>
    private sealed record Sharee(User? User, Group? Group, string Href);

    private async Task SharePostAsync(CalendarNode node, XElement root)
    {
        if (!node.IsOwner || node.Calendar.OwnerId != _user.Id || node.IsInbox)
            throw new DavException(403, D + "need-privileges", "Only the owner can share this calendar");

        var changes = new List<(string Href, ShareAccess? Access, string? Name)>();
        if (root.Name == D + "share-resource")
        {
            foreach (var s in root.Elements(D + "sharee"))
            {
                var href = s.Element(D + "href")?.Value.Trim();
                var access = s.Element(D + "share-access")?.Elements().FirstOrDefault()?.Name;
                if (string.IsNullOrEmpty(href) || access == null) throw new DavException(400, message: "Each sharee needs an href and a share-access");
                ShareAccess? level = access == D + "read-write" ? ShareAccess.ReadWrite
                    : access == D + "read" ? ShareAccess.Read
                    : access == D + "no-access" ? null
                    : throw new DavException(400, message: $"Unsupported share-access {access.LocalName}");
                changes.Add((href, level, null));
            }
        }
        else
        {
            foreach (var set in root.Elements(CS + "set"))
            {
                var href = set.Element(D + "href")?.Value.Trim();
                if (string.IsNullOrEmpty(href)) throw new DavException(400, message: "Each set needs an href");
                changes.Add((href, set.Element(CS + "read-write") != null ? ShareAccess.ReadWrite : ShareAccess.Read, null));
            }
            foreach (var remove in root.Elements(CS + "remove"))
            {
                var href = remove.Element(D + "href")?.Value.Trim();
                if (string.IsNullOrEmpty(href)) throw new DavException(400, message: "Each remove needs an href");
                changes.Add((href, null, null));
            }
        }
        if (changes.Count == 0) throw new DavException(400, message: "The request names nobody to share with");

        // Resolve everyone first so a bad address changes nothing.
        var resolved = new List<(Sharee Who, ShareAccess? Access)>();
        foreach (var (href, access, _) in changes)
        {
            var who = await FindShareeAsync(href);
            if (who == null)
            {
                if (access == null) continue; // removing someone unknown: nothing to do
                throw new DavException(409, message: $"No one on this server has the address {MailtoAddress(href) ?? href}");
            }
            if (who.User?.Id == _user.Id) throw new DavException(409, message: "You can't share a calendar with yourself");
            resolved.Add((who, access));
        }

        var calendar = node.Calendar;
        foreach (var (who, access) in resolved)
        {
            if (access is { } level)
            {
                if (who.User != null) await Calendars.ShareAsync(calendar, who.User, level, Ct);
                else await Calendars.ShareWithGroupAsync(calendar, who.Group!, level, Ct);
            }
            else if (who.User != null)
            {
                await _db.CalendarShares.Where(s => s.CalendarId == calendar.Id && s.ShareeId == who.User.Id).ExecuteDeleteAsync(Ct);
            }
            else
            {
                await _db.CalendarShares.Where(s => s.CalendarId == calendar.Id && s.GroupId == who.Group!.Id).ExecuteDeleteAsync(Ct);
            }
        }
        _log.LogInformation("{User} changed sharing of calendar {Calendar} ({Count} sharees)", _user.Username, calendar.Id, resolved.Count);
        Response.StatusCode = 200;
        Response.ContentLength = 0;
    }

    /// <summary>
    /// A sharee by mailto: address (matched against users' email addresses), or by user or group
    /// principal URL. Disabled accounts can't be shared with.
    /// </summary>
    private async Task<Sharee?> FindShareeAsync(string href)
    {
        if (MailtoAddress(href) is { } email)
        {
            var lower = email.ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.IsEnabled && u.Email != null && u.Email.ToLower() == lower, Ct);
            return user == null ? null : new Sharee(user, null, href);
        }
        switch (Paths.ParseHref(href))
        {
            case ["principals", var username]:
                var byName = await _db.Users.FirstOrDefaultAsync(u => u.IsEnabled && u.Username == username, Ct);
                return byName == null ? null : new Sharee(byName, null, href);
            case ["groups", var groupName]:
                var group = await _db.Groups.FirstOrDefaultAsync(g => g.Name == groupName, Ct);
                return group == null ? null : new Sharee(null, group, href);
            default:
                return null;
        }
    }

    /// <summary>The address in a mailto: URI, or null for anything else.</summary>
    public static string? MailtoAddress(string href)
    {
        href = href.Trim();
        if (!href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
        var address = Uri.UnescapeDataString(href[7..]).Trim();
        return address.Contains('@') ? address : null;
    }
}
