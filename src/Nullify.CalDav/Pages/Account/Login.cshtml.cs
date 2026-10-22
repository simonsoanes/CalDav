// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Account;

public class LoginModel(AppDbContext db, CredentialService credentials, LoginThrottle throttle, SettingsService settings) : AppPageModel
{
    [BindProperty, Required] public string Username { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public string? Error { get; set; }
    [BindProperty(SupportsGet = true)] public bool Local { get; set; }

    public ServerSettings Settings => settings.Current;
    public bool ShowLocal => !Settings.OidcConfigured || !Settings.HideLocalLogin || Local;

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true) return LocalRedirectSafe(ReturnUrl);
        if (!string.IsNullOrEmpty(Error)) ModelState.AddModelError("", Error);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        if (throttle.IsBlocked(ClientKey))
        {
            ModelState.AddModelError("", "Too many failed attempts. Please try again later.");
            return Page();
        }

        var user = await credentials.FindByLoginAsync(db, Username, HttpContext.RequestAborted);
        if (user == null || !user.IsEnabled || !await credentials.VerifyPasswordAsync(user, Password, HttpContext.RequestAborted))
        {
            throttle.RecordFailure(ClientKey);
            ModelState.AddModelError("", "Invalid username or password.");
            return Page();
        }

        throttle.Reset(ClientKey);
        user.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PrincipalFactory.Create(user, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = true });
        return LocalRedirectSafe(ReturnUrl);
    }
}
