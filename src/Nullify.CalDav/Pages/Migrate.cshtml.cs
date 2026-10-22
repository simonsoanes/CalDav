using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages;

public class MigrateModel(AppDbContext db, MigrationService migrations, WriteGate gate) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    /// <summary>Account to import into (administrators only; defaults to the signed-in user).</summary>
    [BindProperty(SupportsGet = true, Name = "user"), Display(Name = "Import into")] public int? ImportInto { get; set; }

    [BindProperty, Display(Name = "Server address")] public string? RemoteUrl { get; set; }
    [BindProperty, Display(Name = "Username")] public string? Username { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "Password")] public string? Password { get; set; }
    [BindProperty] public List<int> Selected { get; set; } = [];

    public List<MigrationSource> Sources { get; private set; } = [];
    public MigrationSource? Source { get; private set; }
    public List<User> Users { get; private set; } = [];
    public Dictionary<int, User> UsersById { get; private set; } = new();
    public bool AllowPrivate { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool IsBusy => Source?.Status is MigrationStatus.Queued or MigrationStatus.Running;

    public int CurrentUserIdValue => CurrentUserId;

    /// <summary>The source belongs to the signed-in user (so links to their collections work).</summary>
    public bool IsOwnSource => Source?.UserId == CurrentUserId;

    public string UserLabel(int? id) =>
        id != null && UsersById.TryGetValue(id.Value, out var u) ? $"{u.DisplayName} ({u.Username})" : "deleted user";

    private async Task LoadAsync()
    {
        var me = await CurrentUserAsync(db);
        IsAdmin = me.IsAdmin;
        AllowPrivate = migrations.AllowPrivate(me);

        // Administrators can see and manage imports for every account; other users only their own.
        var query = db.MigrationSources.AsQueryable();
        if (!IsAdmin) query = query.Where(s => s.UserId == me.Id);
        Sources = await query.OrderByDescending(s => s.CreatedUtc).ToListAsync();
        if (Id != null) Source = await query.Include(s => s.Collections).FirstOrDefaultAsync(s => s.Id == Id);

        Users = await db.Users.OrderBy(u => u.Username).ToListAsync();
        UsersById = Users.ToDictionary(u => u.Id);
        Users = Users.Where(u => u.IsEnabled).ToList();
        if (!IsAdmin || ImportInto == null || !UsersById.ContainsKey(ImportInto.Value)) ImportInto = me.Id;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Id != null && Source == null) return NotFound();
        if (Source != null)
        {
            RemoteUrl = Source.ServerUrl;
            Username = Source.Username;
            ImportInto = Source.UserId;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostConnectAsync()
    {
        await LoadAsync();
        if (Id != null && Source == null) return NotFound();
        if (string.IsNullOrWhiteSpace(RemoteUrl)) ModelState.AddModelError(nameof(RemoteUrl), "Enter the address of the old server.");
        if (string.IsNullOrWhiteSpace(Username)) ModelState.AddModelError(nameof(Username), "Enter the username on the old server.");
        if (string.IsNullOrEmpty(Password)) ModelState.AddModelError(nameof(Password), "Enter the password (or an app password) for the old server.");
        if (!ModelState.IsValid) return Page();

        var me = await CurrentUserAsync(db);
        var targetId = Source?.UserId ?? ImportInto ?? me.Id;
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetId && u.IsEnabled);
        if (target == null || (target.Id != me.Id && !me.IsAdmin))
        {
            ModelState.AddModelError(nameof(ImportInto), "Choose an enabled account to import into.");
            return Page();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            var id = await migrations.ConnectAsync(db, me, target, RemoteUrl!, Username!.Trim(), Password!, Source?.Id, cts.Token);
            StatusMessage = target.Id == me.Id
                ? "Connected. Choose what to import."
                : $"Connected. Choose what to import into {target.DisplayName}'s account.";
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
            await migrations.EnqueueAsync(db, Source.Id, Source.UserId, HttpContext.RequestAborted);
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
        StatusMessage = "The import source and its stored password were removed. Imported data stays in the account.";
        return RedirectToPage(new { id = (int?)null, user = (int?)null });
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
