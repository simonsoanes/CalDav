// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Calendars;

public class SubscribeModel(AppDbContext db, CalendarStore store, SubscriptionService subscriptions, SettingsService settings, WriteGate gate)
    : AppPageModel
{
    [BindProperty, Required, Display(Name = "Calendar URL")] public string FeedUrl { get; set; } = "";
    [BindProperty, Display(Name = "Name")] public string? DisplayName { get; set; }
    [BindProperty] public string Color { get; set; } = "#10B981";
    [BindProperty, Display(Name = "Refresh every")] public int RefreshMinutes { get; set; } = 360;

    public ServerSettings Settings => settings.Current;

    public IActionResult OnGet() => settings.Current.SubscriptionsEnabled ? Page() : RedirectToPage("Index");

    public async Task<IActionResult> OnPostAsync()
    {
        if (!settings.Current.SubscriptionsEnabled) return RedirectToPage("Index");
        var normalized = SubscriptionService.NormalizeUrl(FeedUrl ?? "");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            ModelState.AddModelError(nameof(FeedUrl), "Enter an http(s):// or webcal:// address.");
        if (!Names.IsValidColor(Color)) Color = "#10B981";
        RefreshMinutes = Math.Clamp(RefreshMinutes, settings.Current.SubscriptionMinRefreshMinutes, 7 * 24 * 60);
        if (!ModelState.IsValid) return Page();

        Calendar cal;
        using (await gate.EnterAsync())
        {
            var name = string.IsNullOrWhiteSpace(DisplayName) ? uri!.Host : DisplayName.Trim();
            var slug = await store.UniqueHomeUriAsync(CurrentUserId, Names.Slugify(name, "subscription"));
            cal = new Calendar
            {
                OwnerId = CurrentUserId, Uri = slug, DisplayName = name, Color = Color, Kind = CalendarKind.Subscription,
                SubscriptionUrl = FeedUrl!.Trim(), SubscriptionRefreshMinutes = RefreshMinutes, Components = "VEVENT,VTODO,VJOURNAL",
            };
            db.Calendars.Add(cal);
            await db.SaveChangesAsync();
        }

        var error = await subscriptions.RefreshAsync(cal.Id, HttpContext.RequestAborted);
        if (error == null) StatusMessage = $"Subscribed to \"{cal.DisplayName}\".";
        else ErrorMessage = $"Subscribed, but the first download failed: {error}";
        return RedirectToPage("Edit", new { id = cal.Id });
    }
}
