// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

/// <summary>Sharing calendars from clients: draft-pot-webdav-resource-sharing and CalendarServer CS:share.</summary>
[TestClass]
public sealed class SharingTests
{
    private static Task<(HttpStatusCode Status, string Body)> Send(HttpClient c, string method, string url, string? body = null,
        params (string, string)[] headers) => DavIntegrationTests.SendAsync(c, method, url, body, headers);

    private static async Task<T> WithDbAsync<T>(Func<AppDbContext, IServiceProvider, Task<T>> action)
    {
        using var scope = (await TestHost.GetAsync()).Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider);
    }

    private static async Task<User> CreateUserAsync(string name) =>
        await WithDbAsync(async (db, sp) =>
            await db.Users.FirstOrDefaultAsync(u => u.Username == name)
            ?? await sp.GetRequiredService<UserService>().CreateAsync(name, name.ToUpperInvariant(), name + "@example.com", name + "-password", false));

    private static string ShareResource(string href, string access) =>
        $"""<d:share-resource xmlns:d="DAV:"><d:sharee><d:href>{href}</d:href><d:share-access><d:{access}/></d:share-access></d:sharee></d:share-resource>""";

    private static string Event(string uid) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n" +
        "DTSTART:20261110T100000Z\r\nDTEND:20261110T110000Z\r\nSUMMARY:Shared\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static Task<string?> ShareUriAsync(int calendarOwnerId, string shareeName) =>
        WithDbAsync((db, _) => db.CalendarShares
            .Where(s => s.Calendar.OwnerId == calendarOwnerId && s.Calendar.Uri == "default" && s.Sharee!.Username == shareeName)
            .Select(s => s.Uri).FirstOrDefaultAsync());

    [TestMethod]
    public async Task OptionsAdvertiseBothSharingExtensions()
    {
        var c = await TestHost.ClientAsync();
        var resp = await c.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/dav/"));
        var dav = string.Join(",", resp.Headers.GetValues("DAV"));
        StringAssert.Contains(dav, "resource-sharing");
        StringAssert.Contains(dav, "calendarserver-sharing");
    }

    [TestMethod]
    public async Task ResourceSharing_SharesChangesAndRemoves()
    {
        var owner = await CreateUserAsync("rs-owner");
        await CreateUserAsync("rs-sharee");
        var c = await TestHost.ClientAsync("rs-owner", "rs-owner-password");
        var sharee = await TestHost.ClientAsync("rs-sharee", "rs-sharee-password");
        const string cal = "/dav/calendars/rs-owner/default/";
        const string davSharing = "application/davsharing+xml";

        var (status, _) = await Send(c, "POST", cal, ShareResource("mailto:RS-Sharee@example.com", "read"), ("Content-Type", davSharing));
        Assert.AreEqual(HttpStatusCode.OK, status);

        // The owner sees the sharee in DAV:invite and the calendar as shared.
        var (_, invite) = await Send(c, "PROPFIND", cal,
            """<d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/"><d:prop><d:invite/><d:share-access/><cs:allowed-sharing-modes/></d:prop></d:propfind>""",
            ("Depth", "0"));
        StringAssert.Contains(invite, "mailto:rs-sharee@example.com");
        StringAssert.Contains(invite, "<d:read />");
        StringAssert.Contains(invite, "shared-owner");
        StringAssert.Contains(invite, "can-be-shared");

        // The sharee has it at once, read-only.
        var uri = await ShareUriAsync(owner.Id, "rs-sharee");
        Assert.IsNotNull(uri);
        var (_, access) = await Send(sharee, "PROPFIND", $"/dav/calendars/rs-sharee/{uri}/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:share-access/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(access, "<d:read />");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Send(sharee, "PUT", $"/dav/calendars/rs-sharee/{uri}/a.ics", Event("rs-a"))).Status);

        // Upgrading to read-write lets them add events; the share keeps its place in their home.
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "POST", cal, ShareResource("mailto:rs-sharee@example.com", "read-write"), ("Content-Type", davSharing))).Status);
        Assert.AreEqual(uri, await ShareUriAsync(owner.Id, "rs-sharee"));
        Assert.AreEqual(HttpStatusCode.Created, (await Send(sharee, "PUT", $"/dav/calendars/rs-sharee/{uri}/b.ics", Event("rs-b"))).Status);

        // The sharee can't reshare it.
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await Send(sharee, "POST", $"/dav/calendars/rs-sharee/{uri}/", ShareResource("mailto:alice@example.com", "read"), ("Content-Type", davSharing))).Status);

        // no-access removes the share.
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "POST", cal, ShareResource("mailto:rs-sharee@example.com", "no-access"), ("Content-Type", davSharing))).Status);
        Assert.IsNull(await ShareUriAsync(owner.Id, "rs-sharee"));
        var (_, after) = await Send(c, "PROPFIND", cal, """<d:propfind xmlns:d="DAV:"><d:prop><d:share-access/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(after, "not-shared");
    }

    [TestMethod]
    public async Task ResourceSharing_RejectsUnknownPeopleAndThemselves_WithoutChangingAnything()
    {
        var owner = await CreateUserAsync("rs-careful");
        await CreateUserAsync("rs-known");
        var c = await TestHost.ClientAsync("rs-careful", "rs-careful-password");
        const string cal = "/dav/calendars/rs-careful/default/";
        var both = """
            <d:share-resource xmlns:d="DAV:">
              <d:sharee><d:href>mailto:rs-known@example.com</d:href><d:share-access><d:read/></d:share-access></d:sharee>
              <d:sharee><d:href>mailto:nobody@example.org</d:href><d:share-access><d:read/></d:share-access></d:sharee>
            </d:share-resource>
            """;
        Assert.AreEqual(HttpStatusCode.Conflict, (await Send(c, "POST", cal, both)).Status);
        Assert.IsNull(await ShareUriAsync(owner.Id, "rs-known"), "nothing is shared when one sharee is unknown");
        Assert.AreEqual(HttpStatusCode.Conflict, (await Send(c, "POST", cal, ShareResource("mailto:rs-careful@example.com", "read"))).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "POST", cal, ShareResource("mailto:nobody@example.org", "no-access"))).Status,
            "removing someone who has no share is not an error");
    }

    [TestMethod]
    public async Task CalendarServerSharing_UsersByPrincipalAndGroups()
    {
        var owner = await CreateUserAsync("cs-owner");
        var member = await CreateUserAsync("cs-member");
        await CreateUserAsync("cs-sharee");
        await WithDbAsync(async (db, _) =>
        {
            db.Groups.Add(new Group { Name = "cs-team", DisplayName = "CS Team", Members = [new GroupMember { UserId = member.Id }] });
            return await db.SaveChangesAsync();
        });
        var c = await TestHost.ClientAsync("cs-owner", "cs-owner-password");
        const string cal = "/dav/calendars/cs-owner/default/";

        var share = """
            <cs:share xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
              <cs:set><d:href>/dav/principals/cs-sharee/</d:href><cs:common-name>Ignored</cs:common-name><cs:read-write/></cs:set>
              <cs:set><d:href>/dav/groups/cs-team/</d:href><cs:read/></cs:set>
            </cs:share>
            """;
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "POST", cal, share, ("Content-Type", "application/xml"))).Status);

        var (_, invite) = await Send(c, "PROPFIND", cal,
            """<d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/"><d:prop><cs:invite/></d:prop></d:propfind>""", ("Depth", "0"));
        StringAssert.Contains(invite, "mailto:cs-sharee@example.com");
        StringAssert.Contains(invite, "/dav/groups/cs-team/");
        StringAssert.Contains(invite, "CS Team");
        StringAssert.Contains(invite, "<cs:read-write />");
        StringAssert.Contains(invite, "/dav/principals/cs-owner/");

        // A group member sees it in their own home.
        var memberClient = await TestHost.ClientAsync("cs-member", "cs-member-password");
        var (_, home) = await Send(memberClient, "PROPFIND", "/dav/calendars/cs-member/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:owner/></d:prop></d:propfind>""", ("Depth", "1"));
        StringAssert.Contains(home, "/dav/principals/cs-owner/");

        var remove = """
            <cs:share xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
              <cs:remove><d:href>mailto:cs-sharee@example.com</d:href></cs:remove>
              <cs:remove><d:href>/dav/groups/cs-team/</d:href></cs:remove>
            </cs:share>
            """;
        Assert.AreEqual(HttpStatusCode.OK, (await Send(c, "POST", cal, remove)).Status);
        Assert.AreEqual(0, await WithDbAsync((db, _) => db.CalendarShares.CountAsync(s => s.Calendar.OwnerId == owner.Id)));
    }

    [TestMethod]
    public void MailtoAddress_OnlyAcceptsMailtoUris()
    {
        Assert.AreEqual("bob@example.com", DavHandler.MailtoAddress(" mailto:bob@example.com "));
        Assert.AreEqual("bob+x@example.com", DavHandler.MailtoAddress("MAILTO:bob%2Bx@example.com"));
        Assert.IsNull(DavHandler.MailtoAddress("/dav/principals/bob/"));
        Assert.IsNull(DavHandler.MailtoAddress("mailto:bob"));
    }
}
