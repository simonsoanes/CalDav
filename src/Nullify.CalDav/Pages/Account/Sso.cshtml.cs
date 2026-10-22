using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Account;

[AllowAnonymous]
public class SsoModel(SettingsService settings) : AppPageModel
{
    public IActionResult OnGet(string? returnUrl)
    {
        if (!settings.Current.OidcConfigured) return RedirectToPage("/Account/Login");
        var redirect = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        return Challenge(new AuthenticationProperties { RedirectUri = redirect, IsPersistent = true }, OidcSetup.Scheme);
    }

    /// <summary>Links the signed-in local account to an SSO identity.</summary>
    public IActionResult OnPostLink()
    {
        if (User.Identity?.IsAuthenticated != true) return RedirectToPage("/Account/Login");
        if (!settings.Current.OidcConfigured) return RedirectToPage("/Account/Profile");
        var props = new AuthenticationProperties { RedirectUri = Url.Page("/Account/Profile"), IsPersistent = true };
        props.Items[OidcSetup.LinkUserProperty] = CurrentUserId.ToString();
        return Challenge(props, OidcSetup.Scheme);
    }
}
