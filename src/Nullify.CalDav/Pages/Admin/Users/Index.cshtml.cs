// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Pages.Admin.Users;

public class IndexModel(AppDbContext db) : AppPageModel
{
    public record Row(int Id, string Username, string DisplayName, string? Email, bool IsAdmin, bool IsEnabled, bool Sso,
        bool HasPassword, DateTime? LastLogin, int Calendars, int AddressBooks);

    public List<Row> Users { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Users = await db.Users.OrderBy(u => u.Username)
            .Select(u => new Row(u.Id, u.Username, u.DisplayName, u.Email, u.IsAdmin, u.IsEnabled, u.ExternalSubject != null,
                u.PasswordHash != null, u.LastLoginUtc, u.Calendars.Count, u.AddressBooks.Count))
            .ToListAsync();
    }
}
