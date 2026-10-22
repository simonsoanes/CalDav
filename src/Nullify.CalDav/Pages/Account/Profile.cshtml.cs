// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Account;

public class ProfileModel(AppDbContext db, PasswordService passwords, UserService users, SettingsService settings,
    DavCredentialValidator credentials, CredentialService credentialService, QuotaService quota, WriteGate gate) : AppPageModel
{
    public User Me { get; private set; } = null!;
    public List<AppPassword> AppPasswords { get; private set; } = [];
    public List<SyncMlDevice> Devices { get; private set; } = [];
    public List<Delegation> Delegates { get; private set; } = [];
    public List<User> OtherUsers { get; private set; } = [];
    public long UsedBytes { get; private set; }
    public long? QuotaBytes { get; private set; }

    [BindProperty, Display(Name = "Person")] public string? DelegateUser { get; set; }
    [BindProperty] public bool DelegateCanWrite { get; set; }
    public string SyncMlUrl => Autoconfig.PublicBaseUrl(Request, settings.Current) + "/syncml";
    public string? ImapServer => credentialService.EndpointFor(Me) is { } ep ? $"{ep.Host}:{ep.Port}" : null;
    public string ImapLoginName => CredentialService.ImapLoginFor(Me);
    public ServerSettings Settings => settings.Current;

    [TempData] public string? NewToken { get; set; }

    [BindProperty, Display(Name = "Display name")] public string? DisplayName { get; set; }
    [BindProperty, EmailAddress] public string? Email { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "Current password")] public string? CurrentPassword { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "New password")] public string? NewPassword { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "Confirm new password")] public string? ConfirmPassword { get; set; }
    [BindProperty, Display(Name = "Device name")] public string? TokenName { get; set; }

    private async Task LoadAsync()
    {
        Me = await CurrentUserAsync(db);
        AppPasswords = await db.AppPasswords.Where(a => a.UserId == Me.Id).OrderByDescending(a => a.CreatedUtc).ToListAsync();
        Devices = await db.SyncMlDevices.Where(d => d.UserId == Me.Id).OrderByDescending(d => d.LastSyncUtc).ToListAsync();
        Delegates = await db.Delegations.Include(d => d.Delegate).Where(d => d.OwnerId == Me.Id).OrderBy(d => d.Delegate.Username).ToListAsync();
        var taken = Delegates.Select(d => d.DelegateId).ToHashSet();
        OtherUsers = (await db.Users.Where(u => u.Id != Me.Id && u.IsEnabled).OrderBy(u => u.Username).ToListAsync())
            .Where(u => !taken.Contains(u.Id)).ToList();
        UsedBytes = await quota.UsedAsync(db, Me.Id);
        QuotaBytes = await quota.LimitAsync(db, Me.Id);
    }

    public async Task<IActionResult> OnPostAddDelegateAsync()
    {
        await LoadAsync();
        var target = await db.Users.FirstOrDefaultAsync(u => u.Username == (DelegateUser ?? "") && u.IsEnabled && u.Id != Me.Id);
        if (target == null)
        {
            ErrorMessage = "Select a person.";
            return RedirectToPage();
        }
        using (await gate.EnterAsync())
        {
            db.Delegations.Add(new Delegation { OwnerId = Me.Id, DelegateId = target.Id, CanWrite = DelegateCanWrite });
            await db.SaveChangesAsync();
        }
        StatusMessage = $"{target.DisplayName} can now {(DelegateCanWrite ? "manage" : "see")} all your calendars.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveDelegateAsync(int id)
    {
        using (await gate.EnterAsync())
            await db.Delegations.Where(d => d.Id == id && d.OwnerId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "Delegate removed.";
        return RedirectToPage();
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public async Task<IActionResult> OnGetAppleProfileAsync()
    {
        await LoadAsync();
        var xml = Autoconfig.AppleProfile(Me, Autoconfig.PublicBaseUrl(Request, settings.Current), settings.Current);
        return File(System.Text.Encoding.UTF8.GetBytes(xml), "application/x-apple-aspen-config", Names.Slugify(Me.Username, "account") + ".mobileconfig");
    }

    public async Task<IActionResult> OnPostForgetDeviceAsync(int id)
    {
        await db.SyncMlDevices.Where(d => d.Id == id && d.UserId == CurrentUserId).ExecuteDeleteAsync();
        StatusMessage = "Device forgotten.";
        return RedirectToPage();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        DisplayName = Me.DisplayName;
        Email = Me.Email;
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        await LoadAsync();
        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email))
        {
            ErrorMessage = "Please enter a valid email address.";
            return RedirectToPage();
        }
        Me.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Me.Username : DisplayName.Trim();
        Me.Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        await db.SaveChangesAsync();
        StatusMessage = "Profile updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        await LoadAsync();
        if (Me.AuthSource == AuthSource.Imap)
            ErrorMessage = "Your password is managed by your mail server.";
        else if (Me.PasswordHash != null && !passwords.Verify(Me, CurrentPassword ?? "", out _))
            ErrorMessage = "Your current password is incorrect.";
        else if (PasswordService.ValidateStrength(NewPassword) is { } err)
            ErrorMessage = err;
        else if (NewPassword != ConfirmPassword)
            ErrorMessage = "The new passwords do not match.";
        else
        {
            await users.SetPasswordAsync(Me, NewPassword);
            await HttpContext.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                PrincipalFactory.Create(Me, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme));
            StatusMessage = "Password changed.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateTokenAsync()
    {
        await LoadAsync();
        var name = string.IsNullOrWhiteSpace(TokenName) ? "Device" : TokenName.Trim();
        if (name.Length > 100) name = name[..100];
        var token = PasswordService.GenerateToken();
        db.AppPasswords.Add(new AppPassword { UserId = Me.Id, Name = name, TokenHash = PasswordService.Sha256(token) });
        await db.SaveChangesAsync();
        NewToken = token;
        StatusMessage = $"App password \"{name}\" created. Copy it now - it will not be shown again.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeTokenAsync(int id)
    {
        await db.AppPasswords.Where(a => a.Id == id && a.UserId == CurrentUserId).ExecuteDeleteAsync();
        credentials.InvalidateAll();
        StatusMessage = "App password revoked.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlinkAsync()
    {
        await LoadAsync();
        if (Me.PasswordHash == null)
        {
            ErrorMessage = "Set a password before unlinking single sign-on, otherwise you could not sign in.";
            return RedirectToPage();
        }
        Me.ExternalSubject = null;
        await db.SaveChangesAsync();
        StatusMessage = "Single sign-on identity unlinked.";
        return RedirectToPage();
    }
}
