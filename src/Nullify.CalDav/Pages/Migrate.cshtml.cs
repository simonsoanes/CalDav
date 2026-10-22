using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages;

public class MigrateModel(AppDbContext db, MigrationService migrations, WriteGate gate) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    [BindProperty, Display(Name = "Server address")] public string? RemoteUrl { get; set; }
    [BindProperty, Display(Name = "Username")] public string? Username { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "Password")] public string? Password { get; set; }
    [BindProperty] public List<int> Selected { get; set; } = [];

    public List<MigrationSource> Sources { get; private set; } = [];
    public MigrationSource? Source { get; private set; }
    public bool AllowPrivate { get; private set; }
    public bool IsBusy => Source?.Status is MigrationStatus.Queued or MigrationStatus.Running;

    private async Task LoadAsync()
    {
        Sources = await db.MigrationSources.Where(s => s.UserId == CurrentUserId).OrderByDescending(s => s.CreatedUtc).ToListAsync();
        if (Id != null)
            Source = await db.MigrationSources.Include(s => s.Collections)
                .FirstOrDefaultAsync(s => s.Id == Id && s.UserId == CurrentUserId);
        var me = await CurrentUserAsync(db);
        AllowPrivate = migrations.AllowPrivate(me);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Id != null && Source == null) return NotFound();
        if (Source != null)
        {
            RemoteUrl = Source.ServerUrl;
            Username = Source.Username;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostConnectAsync()
    {
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(RemoteUrl)) ModelState.AddModelError(nameof(RemoteUrl), "Enter the address of your old server.");
        if (string.IsNullOrWhiteSpace(Username)) ModelState.AddModelError(nameof(Username), "Enter your username on the old server.");
        if (string.IsNullOrEmpty(Password)) ModelState.AddModelError(nameof(Password), "Enter your password (or an app password) for the old server.");
        if (!ModelState.IsValid) return Page();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            var id = await migrations.ConnectAsync(db, await CurrentUserAsync(db), RemoteUrl!, Username!.Trim(), Password!, Source?.Id, cts.Token);
            StatusMessage = "Connected. Choose what to import.";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (ex is RemoteDavException or HttpRequestException or TaskCanceledException)
        {
            ModelState.AddModelError("", ex is TaskCanceledException ? "The server did not respond in time." : ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        await LoadAsync();
        if (Source == null) return NotFound();
        if (IsBusy) return RedirectToPage(new { id = Id });
        using (await gate.EnterAsync())
        {
            foreach (var c in Source.Collections) c.Selected = Selected.Contains(c.Id);
            await db.SaveChangesAsync();
        }
        if (Source.Collections.All(c => !c.Selected))
        {
            ErrorMessage = "Select at least one calendar or address book.";
            return RedirectToPage(new { id = Id });
        }
        using (await gate.EnterAsync())
            await migrations.EnqueueAsync(db, Source.Id, CurrentUserId, HttpContext.RequestAborted);
        StatusMessage = "Import started. You can leave this page; it continues in the background.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await LoadAsync();
        if (Source == null) return NotFound();
        if (IsBusy)
        {
            ErrorMessage = "Wait for the import to finish first.";
            return RedirectToPage(new { id = Id });
        }
        using (await gate.EnterAsync())
            await db.MigrationSources.Where(s => s.Id == Source.Id).ExecuteDeleteAsync();
        StatusMessage = "The import source and its stored password were removed. Imported data stays in your account.";
        return RedirectToPage(new { id = (int?)null });
    }

    public static string StatusLabel(MigrationStatus s) => s switch
    {
        MigrationStatus.Ready => "Ready",
        MigrationStatus.Queued => "Queued",
        MigrationStatus.Running => "Importing",
        MigrationStatus.Completed => "Completed",
        MigrationStatus.Failed => "Failed",
        _ => s.ToString(),
    };
}
