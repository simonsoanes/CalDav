// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ------------------------------------------------------------------ Storage
var dataDir = config["Storage:DataDirectory"];
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
dataDir = Path.GetFullPath(dataDir);
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "nullify-caldav.db");

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=True;Default Timeout=30"));
builder.Services.AddDataProtection()
    .SetApplicationName("Nullify.CalDav")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

// ------------------------------------------------------------------ Services
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<WriteGate>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ImapAuthenticator>();
builder.Services.AddSingleton<CredentialService>();
builder.Services.AddSingleton<DavCredentialValidator>();
builder.Services.AddSingleton<Nullify.CalDav.SyncMl.SyncMlSessionStore>();
builder.Services.AddSingleton<CollectionChangeNotifier>();
builder.Services.AddSingleton<OutboundHttp>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddSingleton<SmtpSender>();
builder.Services.AddSingleton<MailQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailQueue>());
builder.Services.AddSingleton<SystemCollectionsService>();
builder.Services.AddSingleton<BirthdayService>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddScoped<SchedulingService>();
builder.Services.AddHostedService<ChangeDispatcher>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddHostedService<MigrationWorker>();
builder.Services.AddSingleton<QuotaService>();
builder.Services.AddScoped<TrashService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CalendarStore>();
builder.Services.AddScoped<AddressBookStore>();
builder.Services.AddScoped<OidcLoginHandler>();
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ConfigureOidcOptions>();

// ------------------------------------------------------------------ Authentication (management UI)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.LogoutPath = "/account/logout";
        o.AccessDeniedPath = "/account/denied";
        o.Cookie.Name = "nullify.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = CookieValidation.ValidateAsync;
    })
    .AddOpenIdConnect(OidcSetup.Scheme, "Single sign-on", _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppClaims.AdminPolicy, p => p.RequireRole(AppClaims.AdminRole));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AuthorizeFolder("/Admin", AppClaims.AdminPolicy);
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Account/Denied");
    o.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddRouting(o => o.LowercaseUrls = true);
builder.Services.AddHealthChecks();

// ------------------------------------------------------------------ Reverse proxy support
// By default the app expects to run behind a TLS-terminating reverse proxy (e.g. Caddy) and trusts
// X-Forwarded-* headers from loopback and private networks (Docker bridge networks included).
var reverseProxyEnabled = config.GetValue("ReverseProxy:Enabled", true);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    if (config["ReverseProxy:ForwardLimit"] is { } limit) o.ForwardLimit = int.TryParse(limit, out var l) && l > 0 ? l : null;
    if (config.GetValue<bool>("ReverseProxy:TrustAll"))
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
    foreach (var proxy in (config["ReverseProxy:KnownProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
    foreach (var network in (config["ReverseProxy:KnownNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (System.Net.IPNetwork.TryParse(network, out var net)) o.KnownIPNetworks.Add(net);
    var hosts = config["ReverseProxy:AllowedHosts"];
    if (!string.IsNullOrWhiteSpace(hosts)) o.AllowedHosts = hosts.Split(',', StringSplitOptions.TrimEntries);
});

var app = builder.Build();

// ------------------------------------------------------------------ Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    app.Services.GetRequiredService<SettingsService>().Load();
    var system = app.Services.GetRequiredService<SystemCollectionsService>();
    app.Services.GetRequiredService<CollectionChangeNotifier>().UsersChanged += system.RequestDirectoryRebuild;
    app.Logger.LogInformation("Using database {Path}", dbPath);
}

// ------------------------------------------------------------------ Pipeline
var pathBase = config["PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase)) app.UsePathBase(pathBase);

if (reverseProxyEnabled) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    if (!ctx.Request.Path.StartsWithSegments("/dav")) ctx.Response.Headers.XFrameOptions = "DENY";
    await next();
});

app.UseMiddleware<SetupGateMiddleware>();

// Service discovery (RFC 6764) and clients that probe the server root.
app.Use((ctx, next) =>
{
    var p = ctx.Request.Path.Value?.TrimEnd('/');
    if (p is not ("/.well-known/caldav" or "/.well-known/carddav")) return next(ctx);
    ctx.Response.Redirect(ctx.Request.PathBase + "/dav/", permanent: true);
    return Task.CompletedTask;
});
app.MapWhen(ctx => ctx.Request.Path == "/" && ctx.Request.Method is "PROPFIND" or "REPORT",
    a => a.Run(ctx => { ctx.Response.Redirect(ctx.Request.PathBase + "/dav/", permanent: true); return Task.CompletedTask; }));
app.Map("/dav", dav => dav.Run(DavHandler.HandleAsync));

// SyncML (OMA DS 1.1/1.2) over HTTP; common client default paths are accepted.
foreach (var syncPath in new[] { "/syncml", "/sync", "/funambol/ds" })
    app.Map(syncPath, s => s.Run(Nullify.CalDav.SyncMl.SyncMlEndpoint.HandleAsync));

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapAutoconfig();

// Public read-only iCalendar feeds (webcal links) for calendars published by their owners.
app.MapGet("/public/calendar/{token}", async (string token, AppDbContext db, CalendarStore store, SettingsService settings, HttpContext ctx) =>
{
    if (!settings.Current.PublicLinksEnabled) return Results.NotFound();
    token = token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? token[..^4] : token;
    if (token.Length < 20) return Results.NotFound();
    var cal = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.PublicToken == token && c.Owner.IsEnabled);
    if (cal == null) return Results.NotFound();
    var ics = await store.ExportAsync(cal, ctx.RequestAborted, busyOnly: cal.PublicBusyOnly);
    ctx.Response.Headers.CacheControl = "private, max-age=300";
    return Results.Text(ics, "text/calendar; charset=utf-8");
}).AllowAnonymous();
app.MapRazorPages();

app.Run();

public partial class Program;
