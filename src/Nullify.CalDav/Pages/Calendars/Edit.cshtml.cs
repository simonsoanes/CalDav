using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Calendars;

public class EditModel(AppDbContext db, CalendarStore store, WriteGate gate, SettingsService settings, SubscriptionService subscriptions)
    : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    [BindProperty, Required, Display(Name = "Name"), StringLength(200)] public string DisplayName { get; set; } = "";
    [BindProperty, StringLength(2000)] public string? Description { get; set; }
    [BindProperty] public string Color { get; set; } = "#3B82F6";
    [BindProperty] public bool Events { get; set; } = true;
    [BindProperty] public bool Tasks { get; set; } = true;
    [BindProperty] public bool Notes { get; set; } = true;
    [BindProperty, Display(Name = "URL name")] public string? Uri { get; set; }
    [BindProperty, Display(Name = "Calendar URL")] public string? SubscriptionUrl { get; set; }
    [BindProperty, Display(Name = "Refresh every")] public int RefreshMinutes { get; set; } = 360;

    [BindProperty, Display(Name = "Share with")] public string? ShareWith { get; set; }
    [BindProperty] public ShareAccess ShareAccess { get; set; } = ShareAccess.Read;
    [BindProperty, Display(Name = "Only publish busy times")] public bool PublicBusyOnly { get; set; }

    public Calendar? Calendar { get; private set; }
    public int ObjectCount { get; private set; }
    public List<CalendarShare> Shares { get; private set; } = [];
    public List<User> OtherUsers { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public string? CollectionUrl { get; private set; }
    public ServerSettings Settings => settings.Current;
    public bool IsNormal => Calendar == null || Calendar.Kind == CalendarKind.Normal;

    public string? PublicUrl => Calendar?.PublicToken == null ? null
        : Autoconfig.PublicBaseUrl(Request, settings.Current) + "/public/calendar/" + Calendar.PublicToken + ".ics";

    public string? WebcalUrl => PublicUrl == null ? null : "webcal" + PublicUrl[PublicUrl.IndexOf("://", StringComparison.Ordinal)..];

    private async Task<Calendar?> LoadAsync()
    {
        if (Id == null) return null;
        Calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == Id && c.OwnerId == CurrentUserId && c.Kind != CalendarKind.Inbox);
        if (Calendar == null) return null;
        ObjectCount = await db.CalendarObjects.CountAsync(o => o.CalendarId == Calendar.Id);
        Shares = await db.CalendarShares.Include(s => s.Sharee).Include(s => s.Group).Where(s => s.CalendarId == Calendar.Id)
            .OrderBy(s => s.GroupId == null).ThenBy(s => s.Id).ToListAsync();
        var sharedUsers = Shares.Where(s => s.ShareeId != null).Select(s => s.ShareeId!.Value).ToHashSet();
        var sharedGroups = Shares.Where(s => s.GroupId != null).Select(s => s.GroupId!.Value).ToHashSet();
        OtherUsers = (await db.Users.Where(u => u.Id != CurrentUserId && u.IsEnabled).OrderBy(u => u.Username).ToListAsync())
            .Where(u => !sharedUsers.Contains(u.Id)).ToList();
        Groups = (await db.Groups.OrderBy(g => g.DisplayName).ToListAsync()).Where(g => !sharedGroups.Contains(g.Id)).ToList();
        var me = await db.Users.Where(u => u.Id == CurrentUserId).Select(u => u.Username).FirstAsync();
        CollectionUrl = new DavPaths(Autoconfig.PublicBaseUrl(Request, settings.Current) + "/dav").Calendar(me, Calendar.Uri);
        return Calendar;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id == null) return Page();
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        DisplayName = cal.DisplayName;
        Description = cal.Description;
        Color = Names.IsValidColor(cal.Color) && cal.Color!.Length >= 7 ? cal.Color[..7] : "#3B82F6";
        var comps = cal.ComponentList;
        Events = comps.Contains("VEVENT");
        Tasks = comps.Contains("VTODO");
        Notes = comps.Contains("VJOURNAL");
        Uri = cal.Uri;
        SubscriptionUrl = cal.SubscriptionUrl;
        RefreshMinutes = cal.SubscriptionRefreshMinutes;
        PublicBusyOnly = cal.PublicBusyOnly;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var cal = await LoadAsync();
        if (Id != null && cal == null) return NotFound();

        var components = new List<string>();
        if (Events) components.Add("VEVENT");
        if (Tasks) components.Add("VTODO");
        if (Notes) components.Add("VJOURNAL");
        if (components.Count == 0 && IsNormal) ModelState.AddModelError("", "Select at least one type of content.");
        if (!Names.IsValidColor(Color)) Color = "#3B82F6";
        if (cal?.Kind == CalendarKind.Subscription)
        {
            var normalized = SubscriptionService.NormalizeUrl(SubscriptionUrl ?? "");
            if (!System.Uri.TryCreate(normalized, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                ModelState.AddModelError(nameof(SubscriptionUrl), "Enter an http(s):// or webcal:// address.");
        }
        if (!ModelState.IsValid) return Page();

        using (await gate.EnterAsync())
        {
            if (cal == null)
            {
                var requested = string.IsNullOrWhiteSpace(Uri) ? Names.Slugify(DisplayName, "calendar") : Uri.Trim();
                if (!Names.IsValidSegment(requested))
                {
                    ModelState.AddModelError(nameof(Uri), "Invalid URL name.");
                    return Page();
                }
                if (!string.IsNullOrWhiteSpace(Uri) && await store.HomeUriTakenAsync(CurrentUserId, requested))
                {
                    ModelState.AddModelError(nameof(Uri), "That URL name is already in use.");
                    return Page();
                }
                var uri = await store.UniqueHomeUriAsync(CurrentUserId, requested);
                cal = await store.CreateAsync(CurrentUserId, uri, DisplayName.Trim(), components, Description, Color);
                StatusMessage = "Calendar created.";
            }
            else
            {
                cal.DisplayName = DisplayName.Trim();
                cal.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
                cal.Color = Color;
                if (cal.Kind == CalendarKind.Normal) cal.Components = string.Join(',', components);
                if (cal.Kind == CalendarKind.Subscription)
                {
                    if (cal.SubscriptionUrl != SubscriptionUrl!.Trim()) cal.SubscriptionETag = null;
                    cal.SubscriptionUrl = SubscriptionUrl.Trim();
                    cal.SubscriptionRefreshMinutes = Math.Clamp(RefreshMinutes, settings.Current.SubscriptionMinRefreshMinutes, 7 * 24 * 60);
                }
                cal.ModifiedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                StatusMessage = "Calendar saved.";
            }
        }
        return RedirectToPage(new { id = cal.Id });
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        var cal = await LoadAsync();
        if (cal is not { Kind: CalendarKind.Subscription }) return NotFound();
        var error = await subscriptions.RefreshAsync(cal.Id, HttpContext.RequestAborted);
        if (error == null) StatusMessage = "Subscription refreshed.";
        else ErrorMessage = "Refresh failed: " + error;
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostShareAsync()
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        var target = (ShareWith ?? "").Trim();
        using (await gate.EnterAsync())
        {
            if (target.StartsWith("group:", StringComparison.Ordinal))
            {
                var groupName = target[6..];
                var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == groupName);
                if (group == null) { ErrorMessage = "Select a valid group."; return RedirectToPage(new { id = Id }); }
                await store.ShareWithGroupAsync(cal, group, ShareAccess);
                StatusMessage = $"Shared with group {group.DisplayName}.";
            }
            else
            {
                var sharee = await db.Users.FirstOrDefaultAsync(u => u.Username == target.Replace("user:", "") && u.IsEnabled);
                if (sharee == null || sharee.Id == CurrentUserId) { ErrorMessage = "Select a valid user to share with."; return RedirectToPage(new { id = Id }); }
                await store.ShareAsync(cal, sharee, ShareAccess);
                StatusMessage = $"Shared with {sharee.DisplayName}.";
            }
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostAccessAsync(int shareId, ShareAccess access)
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        var share = Shares.FirstOrDefault(s => s.Id == shareId);
        if (share != null)
        {
            share.Access = access;
            using (await gate.EnterAsync()) await db.SaveChangesAsync();
            StatusMessage = "Access updated.";
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUnshareAsync(int shareId)
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        using (await gate.EnterAsync())
            await db.CalendarShares.Where(s => s.Id == shareId && s.CalendarId == cal.Id).ExecuteDeleteAsync();
        StatusMessage = "Sharing removed.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostPublishAsync(bool regenerate)
    {
        var cal = await LoadAsync();
        if (cal == null || !settings.Current.PublicLinksEnabled) return NotFound();
        using (await gate.EnterAsync())
        {
            if (cal.PublicToken == null || regenerate)
                cal.PublicToken = PushService.Base64Url(RandomNumberGenerator.GetBytes(24));
            cal.PublicBusyOnly = PublicBusyOnly;
            await db.SaveChangesAsync();
        }
        StatusMessage = regenerate ? "A new public link was created; the old one no longer works." : "Public link updated.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUnpublishAsync()
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        using (await gate.EnterAsync())
        {
            cal.PublicToken = null;
            await db.SaveChangesAsync();
        }
        StatusMessage = "The public link was disabled.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        var cal = await LoadAsync();
        if (cal is not { Kind: CalendarKind.Normal }) return NotFound();
        if (file == null || file.Length == 0)
        {
            ErrorMessage = "Choose an .ics file to import.";
            return RedirectToPage(new { id = Id });
        }
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true)) text = await reader.ReadToEndAsync();

        CalendarStore.ImportResult result;
        using (await gate.EnterAsync()) result = await store.ImportAsync(cal, text);
        StatusMessage = $"Import finished: {result.Imported} added, {result.Updated} updated, {result.Skipped} skipped.";
        if (result.Errors.Count > 0) ErrorMessage = string.Join(" ", result.Errors.Take(5));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        var ics = await store.ExportAsync(cal);
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar", Names.Slugify(cal.DisplayName, "calendar") + ".ics");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var cal = await LoadAsync();
        if (cal == null) return NotFound();
        if (cal.Kind == CalendarKind.Birthdays)
        {
            ErrorMessage = "The birthday calendar is generated by the server and cannot be deleted.";
            return RedirectToPage(new { id = Id });
        }
        using (await gate.EnterAsync()) await store.DeleteAsync(cal);
        StatusMessage = cal.Kind == CalendarKind.Normal
            ? $"Calendar \"{cal.DisplayName}\" moved to the trash."
            : $"Unsubscribed from \"{cal.DisplayName}\".";
        return RedirectToPage("Index");
    }
}
