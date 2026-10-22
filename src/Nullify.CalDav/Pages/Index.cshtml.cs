// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Pages;

public class IndexModel(AppDbContext db) : AppPageModel
{
    public User Me { get; private set; } = null!;
    public List<(string Name, string? Color, string Url, string Kind, int Count, bool Shared)> Calendars { get; } = [];
    public List<(string Name, string Url, int Count, bool Shared)> AddressBooks { get; } = [];

    public string PrincipalUrl => ServerUrl + new DavPaths("/dav").Principal(Me.Username);

    public async Task OnGetAsync()
    {
        Me = await CurrentUserAsync(db);
        var paths = new DavPaths(ServerUrl + "/dav");

        var cals = await db.Calendars.Where(c => c.OwnerId == Me.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new { c.DisplayName, c.Color, c.Uri, c.Components, Count = c.Objects.Count }).ToListAsync();
        foreach (var c in cals) Calendars.Add((c.DisplayName, c.Color, paths.Calendar(Me.Username, c.Uri), c.Components, c.Count, false));

        var sharedCals = await db.CalendarShares.Where(s => s.ShareeId == Me.Id)
            .Select(s => new { Name = s.DisplayName ?? s.Calendar.DisplayName, Color = s.Color ?? s.Calendar.Color, s.Uri, s.Calendar.Components, Count = s.Calendar.Objects.Count })
            .ToListAsync();
        foreach (var c in sharedCals) Calendars.Add((c.Name, c.Color, paths.Calendar(Me.Username, c.Uri), c.Components, c.Count, true));

        var books = await db.AddressBooks.Where(b => b.OwnerId == Me.Id).OrderBy(b => b.Id)
            .Select(b => new { b.DisplayName, b.Uri, Count = b.Cards.Count }).ToListAsync();
        foreach (var b in books) AddressBooks.Add((b.DisplayName, paths.AddressBook(Me.Username, b.Uri), b.Count, false));

        var sharedBooks = await db.AddressBookShares.Where(s => s.ShareeId == Me.Id)
            .Select(s => new { Name = s.DisplayName ?? s.AddressBook.DisplayName, s.Uri, Count = s.AddressBook.Cards.Count }).ToListAsync();
        foreach (var b in sharedBooks) AddressBooks.Add((b.Name, paths.AddressBook(Me.Username, b.Uri), b.Count, true));
    }
}
