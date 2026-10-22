// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.AddressBooks;

public class IndexModel(AppDbContext db, WriteGate gate) : AppPageModel
{
    public record Row(int Id, string Name, string? Description, int Count, int ShareCount);
    public record SharedRow(int ShareId, string Name, string Owner, ShareAccess Access, int Count);

    public List<Row> Owned { get; private set; } = [];
    public List<SharedRow> Shared { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = CurrentUserId;
        Owned = await db.AddressBooks.Where(b => b.OwnerId == me).OrderBy(b => b.Id)
            .Select(b => new Row(b.Id, b.DisplayName, b.Description, b.Cards.Count, b.Shares.Count)).ToListAsync();
        var groups = await db.GroupMembers.Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync();
        Shared = await db.AddressBookShares
            .Where(s => s.AddressBook.OwnerId != me && (s.ShareeId == me || (s.GroupId != null && groups.Contains(s.GroupId.Value))))
            .Select(s => new SharedRow(s.Id, s.DisplayName ?? s.AddressBook.DisplayName, s.AddressBook.Owner.DisplayName, s.Access, s.AddressBook.Cards.Count))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostLeaveAsync(int shareId)
    {
        using (await gate.EnterAsync())
            await db.AddressBookShares.Where(s => s.Id == shareId && s.ShareeId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "You no longer have access to that address book.";
        return RedirectToPage();
    }
}
