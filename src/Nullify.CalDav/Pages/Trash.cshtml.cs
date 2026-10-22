using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages;

public class TrashModel(AppDbContext db, TrashService trash, WriteGate gate, SettingsService settings) : AppPageModel
{
    public record Row(long Id, TrashKind Kind, string Title, string CollectionName, DateTime DeletedUtc, long Size);

    public List<Row> Items { get; private set; } = [];
    public int RetentionDays => settings.Current.TrashRetentionDays;

    public async Task OnGetAsync()
    {
        Items = await db.TrashItems.Where(t => t.OwnerId == CurrentUserId).OrderByDescending(t => t.DeletedUtc)
            .Select(t => new Row(t.Id, t.Kind, t.Title, t.CollectionName, t.DeletedUtc, t.Size)).Take(500).ToListAsync();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id)
    {
        try
        {
            (bool Ok, string Message) result;
            using (await gate.EnterAsync()) result = await trash.RestoreAsync(id, CurrentUserId);
            if (result.Ok) StatusMessage = result.Message; else ErrorMessage = result.Message;
        }
        catch (QuotaExceededException)
        {
            ErrorMessage = "Restoring this item would exceed your storage quota.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        using (await gate.EnterAsync())
            await db.TrashItems.Where(t => t.Id == id && t.OwnerId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "Deleted permanently.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEmptyAsync()
    {
        using (await gate.EnterAsync())
            await db.TrashItems.Where(t => t.OwnerId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "Trash emptied.";
        return RedirectToPage();
    }

    public static string Label(TrashKind kind) => kind switch
    {
        TrashKind.CalendarObject => "Calendar item",
        TrashKind.Card => "Contact",
        TrashKind.Calendar => "Calendar",
        TrashKind.AddressBook => "Address book",
        _ => kind.ToString(),
    };
}
