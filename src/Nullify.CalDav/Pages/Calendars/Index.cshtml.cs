using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Calendars;

public class IndexModel(AppDbContext db, WriteGate gate, SettingsService settings) : AppPageModel
{
    public record Row(int Id, string Name, string? Color, string Components, int Count, int ShareCount, CalendarKind Kind,
        string? SubscriptionUrl, DateTime? LastFetch, string? Error, bool Public);
    public record SharedRow(int ShareId, string Name, string? Color, string Components, string Owner, ShareAccess Access, string? Group);
    public record DelegatedRow(string Owner, string Username, bool CanWrite, int Calendars);

    public List<Row> Owned { get; private set; } = [];
    public List<SharedRow> Shared { get; private set; } = [];
    public List<DelegatedRow> Delegated { get; private set; } = [];
    public ServerSettings Settings => settings.Current;

    public async Task OnGetAsync()
    {
        var me = CurrentUserId;
        Owned = await db.Calendars.Where(c => c.OwnerId == me && c.Kind != CalendarKind.Inbox).OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new Row(c.Id, c.DisplayName, c.Color, c.Components, c.Objects.Count, c.Shares.Count, c.Kind,
                c.SubscriptionUrl, c.SubscriptionLastFetchUtc, c.SubscriptionError, c.PublicToken != null)).ToListAsync();
        var groups = await db.GroupMembers.Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync();
        Shared = await db.CalendarShares
            .Where(s => s.Calendar.OwnerId != me && (s.ShareeId == me || (s.GroupId != null && groups.Contains(s.GroupId.Value))))
            .Select(s => new SharedRow(s.Id, s.DisplayName ?? s.Calendar.DisplayName, s.Color ?? s.Calendar.Color, s.Calendar.Components,
                s.Calendar.Owner.DisplayName, s.Access, s.Group != null ? s.Group.DisplayName : null))
            .ToListAsync();
        Delegated = await db.Delegations.Where(d => d.DelegateId == me && d.Owner.IsEnabled)
            .Select(d => new DelegatedRow(d.Owner.DisplayName, d.Owner.Username, d.CanWrite,
                db.Calendars.Count(c => c.OwnerId == d.OwnerId && c.Kind != CalendarKind.Inbox)))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostLeaveAsync(int shareId)
    {
        using (await gate.EnterAsync())
            await db.CalendarShares.Where(s => s.Id == shareId && s.ShareeId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "You no longer have access to that calendar.";
        return RedirectToPage();
    }

    public static string Describe(string components) => string.Join(", ", components.Split(',').Select(c => c switch
    {
        "VEVENT" => "Events",
        "VTODO" => "Tasks",
        "VJOURNAL" => "Notes",
        _ => c,
    }));
}
