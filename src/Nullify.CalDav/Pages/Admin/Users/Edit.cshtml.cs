using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Admin.Users;

public class EditModel(AppDbContext db, UserService users, WriteGate gate, SettingsService settings) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    [BindProperty, Display(Name = "Username")] public string? Username { get; set; }
    [BindProperty, Display(Name = "Display name")] public string? DisplayName { get; set; }
    [BindProperty, EmailAddress] public string? Email { get; set; }
    [BindProperty, Display(Name = "Administrator")] public bool IsAdmin { get; set; }
    [BindProperty, Display(Name = "Enabled")] public bool IsEnabled { get; set; } = true;
    [BindProperty, DataType(DataType.Password), Display(Name = "Password")] public string? Password { get; set; }
    [BindProperty, Display(Name = "Password verification")] public AuthSource AuthSource { get; set; } = AuthSource.Local;
    [BindProperty, Display(Name = "IMAP login")] public string? ImapLogin { get; set; }
    [BindProperty, Display(Name = "IMAP server")] public string? ImapHost { get; set; }
    [BindProperty, Display(Name = "Port")] public int? ImapPort { get; set; }
    [BindProperty, Display(Name = "Security")] public MailSecurity? ImapSecurity { get; set; }

    [BindProperty, Display(Name = "Storage quota (MB)")] public long? QuotaMb { get; set; }

    public ServerSettings Settings => settings.Current;

    public User? Target { get; private set; }
    public bool IsSelf => Target?.Id == CurrentUserId;

    private async Task<User?> LoadAsync() =>
        Target = Id == null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == Id);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id == null) return Page();
        var u = await LoadAsync();
        if (u == null) return NotFound();
        Username = u.Username;
        DisplayName = u.DisplayName;
        Email = u.Email;
        IsAdmin = u.IsAdmin;
        IsEnabled = u.IsEnabled;
        AuthSource = u.AuthSource;
        ImapLogin = u.ImapLogin;
        ImapHost = u.ImapHost;
        ImapPort = u.ImapPort;
        ImapSecurity = u.ImapSecurity;
        QuotaMb = u.QuotaBytes == null ? null : u.QuotaBytes / (1024 * 1024);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var u = await LoadAsync();
        if (Id != null && u == null) return NotFound();

        if (!string.IsNullOrEmpty(Password) && PasswordService.ValidateStrength(Password) is { } pwErr)
            ModelState.AddModelError(nameof(Password), pwErr);
        ImapHost = string.IsNullOrWhiteSpace(ImapHost) ? null : ImapHost.Trim();
        ImapLogin = string.IsNullOrWhiteSpace(ImapLogin) ? null : ImapLogin.Trim();
        if (ImapHost != null && (ImapHost.Contains('/') || ImapHost.Contains(' ') || Uri.CheckHostName(ImapHost) == UriHostNameType.Unknown))
            ModelState.AddModelError(nameof(ImapHost), "Enter a host name or IP address.");
        if (ImapPort is < 1 or > 65535) ModelState.AddModelError(nameof(ImapPort), "Invalid port.");
        if (AuthSource == AuthSource.Imap && ImapHost == null && !settings.Current.ImapConfigured)
            ModelState.AddModelError(nameof(AuthSource), "Configure a server-wide IMAP server under Settings or enter one for this account.");

        using var _ = await gate.EnterAsync();
        if (u == null)
        {
            Username = Username?.Trim();
            if (!Names.IsValidUsername(Username))
                ModelState.AddModelError(nameof(Username), "Usernames may contain letters, digits, '.', '_', '-' and '@' (max 64).");
            else if (await db.Users.AnyAsync(x => x.Username == Username))
                ModelState.AddModelError(nameof(Username), "That username is already taken.");
            if (!ModelState.IsValid) return Page();

            u = await users.CreateAsync(Username!, DisplayName, Email, string.IsNullOrEmpty(Password) ? null : Password, IsAdmin);
            u.IsEnabled = IsEnabled;
            ApplyImap(u);
            await db.SaveChangesAsync();
            StatusMessage = $"User {u.Username} created.";
            return RedirectToPage(new { id = u.Id });
        }

        if (IsSelf && (!IsAdmin || !IsEnabled))
            ModelState.AddModelError("", "You cannot remove your own administrator rights or disable your own account.");
        if (!ModelState.IsValid) return Page();

        var stampChange = u.IsAdmin != IsAdmin || u.IsEnabled != IsEnabled || u.AuthSource != AuthSource;
        ApplyImap(u);
        u.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? u.Username : DisplayName.Trim();
        u.Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        u.IsAdmin = IsAdmin;
        u.IsEnabled = IsEnabled;
        await db.SaveChangesAsync();
        if (!string.IsNullOrEmpty(Password)) await users.SetPasswordAsync(u, Password);
        else if (stampChange) await users.TouchStampAsync(u);
        StatusMessage = "User saved.";
        return RedirectToPage(new { id = u.Id });
    }

    private void ApplyImap(User u)
    {
        u.AuthSource = AuthSource;
        u.ImapLogin = ImapLogin;
        u.ImapHost = ImapHost;
        u.ImapPort = ImapHost == null ? null : ImapPort;
        u.ImapSecurity = ImapHost == null ? null : ImapSecurity;
        u.QuotaBytes = QuotaMb is { } mb && mb >= 0 ? mb * 1024 * 1024 : null;
    }

    public async Task<IActionResult> OnPostUnlinkAsync()
    {
        var u = await LoadAsync();
        if (u == null) return NotFound();
        using (await gate.EnterAsync())
        {
            u.ExternalSubject = null;
            await db.SaveChangesAsync();
        }
        StatusMessage = "SSO identity unlinked.";
        return RedirectToPage(new { id = u.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var u = await LoadAsync();
        if (u == null) return NotFound();
        if (IsSelf)
        {
            ErrorMessage = "You cannot delete your own account.";
            return RedirectToPage(new { id = u.Id });
        }
        using (await gate.EnterAsync()) await users.DeleteAsync(u);
        StatusMessage = $"User {u.Username} and all their data were deleted.";
        return RedirectToPage("Index");
    }
}
