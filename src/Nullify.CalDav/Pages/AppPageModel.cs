// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages;

public abstract class AppPageModel : PageModel
{
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    protected int CurrentUserId => User.UserId() ?? throw new InvalidOperationException("Not signed in");

    protected async Task<User> CurrentUserAsync(AppDbContext db) =>
        await db.Users.FirstAsync(u => u.Id == CurrentUserId);

    /// <summary>Absolute base URL of the server as seen by the browser (honours forwarded headers).</summary>
    public string ServerUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    public string DavUrl => ServerUrl + "/dav/";

    protected IActionResult LocalRedirectSafe(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Index");

    protected string ClientKey => "ui:" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}
