using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

public static class OidcSetup
{
    public const string Scheme = "oidc";
    public const string LinkUserProperty = "nullify:link-user";
}

/// <summary>
/// Configures the OpenID Connect handler from database settings. The options cache is cleared
/// whenever settings are saved, so changes in the admin UI apply without restarting.
/// </summary>
public sealed class ConfigureOidcOptions(SettingsService settings) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (name != OidcSetup.Scheme) return;
        var s = settings.Current;

        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.CallbackPath = "/signin-oidc";
        o.SignedOutCallbackPath = "/signout-callback-oidc";
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        o.SaveTokens = false;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.CorrelationCookie.SameSite = SameSiteMode.None;
        o.NonceCookie.SameSite = SameSiteMode.None;

        if (!s.OidcConfigured)
        {
            // Placeholder values keep the handler valid while SSO is disabled; it is never challenged.
            o.Authority = "https://oidc.invalid/";
            o.ClientId = "unconfigured";
            return;
        }

        o.Authority = s.OidcAuthority!.Trim();
        o.ClientId = s.OidcClientId!.Trim();
        o.ClientSecret = string.IsNullOrEmpty(s.OidcClientSecret) ? null : s.OidcClientSecret;
        o.RequireHttpsMetadata = s.OidcRequireHttpsMetadata;
        o.Scope.Clear();
        foreach (var scope in s.OidcScopes.Split(' ', ',', StringSplitOptions.RemoveEmptyEntries))
            o.Scope.Add(scope);
        if (!o.Scope.Contains("openid")) o.Scope.Add("openid");
        if (!string.IsNullOrWhiteSpace(s.OidcAdminClaimType))
            o.ClaimActions.MapJsonKey(s.OidcAdminClaimType, s.OidcAdminClaimType);
        o.ClaimActions.MapJsonKey(s.OidcUsernameClaim, s.OidcUsernameClaim);
        o.ClaimActions.MapJsonKey("email", "email");
        o.ClaimActions.MapJsonKey("name", "name");

        o.Events = new OpenIdConnectEvents
        {
            OnTicketReceived = ctx => ctx.HttpContext.RequestServices.GetRequiredService<OidcLoginHandler>().HandleAsync(ctx),
            OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/account/login?error=" + Uri.EscapeDataString("Single sign-on failed: " + ctx.Failure?.Message));
                ctx.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }
}

/// <summary>Maps an authenticated OpenID Connect identity onto a local user account.</summary>
public sealed class OidcLoginHandler(AppDbContext db, SettingsService settings, UserService users, ILogger<OidcLoginHandler> log)
{
    public async Task HandleAsync(TicketReceivedContext ctx)
    {
        var s = settings.Current;
        var external = ctx.Principal!;
        var sub = external.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
        {
            Fail(ctx, "The identity provider did not return a subject identifier.");
            return;
        }
        var issuer = external.FindFirst("sub")?.Issuer ?? s.OidcAuthority ?? "";
        var externalId = $"{issuer}|{sub}";
        var preferred = external.FindFirstValue(s.OidcUsernameClaim) ?? external.FindFirstValue("email");
        var email = external.FindFirstValue("email");
        var name = external.FindFirstValue("name");

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == externalId);

        // Explicit account linking initiated from the profile page.
        if (ctx.Properties?.Items.TryGetValue(OidcSetup.LinkUserProperty, out var linkId) == true && int.TryParse(linkId, out var linkUserId))
        {
            // Remote handlers run before the default scheme, so authenticate the cookie explicitly.
            var cookie = await ctx.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var current = cookie.Principal?.UserId();
            if (current != linkUserId)
            {
                Fail(ctx, "Account linking session mismatch.");
                return;
            }
            if (user != null && user.Id != linkUserId)
            {
                Fail(ctx, "This SSO identity is already linked to another account.");
                return;
            }
            user = await db.Users.FirstAsync(u => u.Id == linkUserId);
            user.ExternalSubject = externalId;
            await db.SaveChangesAsync();
            log.LogInformation("Linked SSO identity to user {User}", user.Username);
        }

        if (user == null && s.OidcLinkByUsername && !string.IsNullOrEmpty(preferred))
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Username == preferred && u.ExternalSubject == null);
            if (user != null)
            {
                user.ExternalSubject = externalId;
                log.LogInformation("Linked SSO identity to existing user {User} by username", user.Username);
            }
        }

        if (user == null)
        {
            if (!s.OidcAutoProvision)
            {
                Fail(ctx, "No account is linked to this SSO identity. Ask an administrator to create one.");
                return;
            }
            var username = await UniqueUsernameAsync(preferred ?? sub);
            user = await users.CreateAsync(username, name, email, null, false, externalId);
            log.LogInformation("Provisioned user {User} from SSO", username);
        }

        if (!user.IsEnabled)
        {
            Fail(ctx, "This account is disabled.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(s.OidcAdminClaimType) && !string.IsNullOrWhiteSpace(s.OidcAdminClaimValue))
        {
            var isAdmin = external.FindAll(s.OidcAdminClaimType).Any(c =>
                c.Value.Split(' ', ',').Contains(s.OidcAdminClaimValue, StringComparer.OrdinalIgnoreCase));
            if (isAdmin != user.IsAdmin && (isAdmin || !await users.IsLastAdminAsync(user)))
            {
                user.IsAdmin = isAdmin;
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }

        if (string.IsNullOrEmpty(user.Email) && !string.IsNullOrEmpty(email)) user.Email = email;
        user.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        ctx.Principal = PrincipalFactory.Create(user, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private async Task<string> UniqueUsernameAsync(string seed)
    {
        var at = seed.IndexOf('@');
        var basis = new string((at > 0 ? seed[..at] : seed).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (basis.Length == 0 || !char.IsAsciiLetterOrDigit(basis[0])) basis = "user" + basis;
        if (basis.Length > 56) basis = basis[..56];
        var candidate = basis;
        for (var i = 2; await db.Users.AnyAsync(u => u.Username == candidate); i++) candidate = basis + i;
        return candidate;
    }

    private static void Fail(TicketReceivedContext ctx, string message)
    {
        ctx.Response.Redirect("/account/login?error=" + Uri.EscapeDataString(message));
        ctx.HandleResponse();
    }
}

public static class CookieValidation
{
    /// <summary>Rejects sessions for deleted/disabled users or after credential changes; refreshes role claims.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        if (id == null)
        {
            ctx.RejectPrincipal();
            return;
        }
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        var stamp = ctx.Principal!.FindFirstValue(AppClaims.Stamp);
        if (user is not { IsEnabled: true } || user.SecurityStamp != stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var displayName = string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName;
        if (ctx.Principal!.FindFirstValue("display_name") != displayName || ctx.Principal!.Identity?.Name != user.Username)
        {
            ctx.ReplacePrincipal(PrincipalFactory.Create(user, CookieAuthenticationDefaults.AuthenticationScheme));
            ctx.ShouldRenew = true;
        }
    }
}

/// <summary>Redirects everything to the first-run setup page until an administrator exists.</summary>
public sealed class SetupGateMiddleware(RequestDelegate next)
{
    private static volatile bool _complete;

    public static void MarkComplete() => _complete = true;

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        if (!_complete)
        {
            if (await db.Users.AnyAsync()) _complete = true;
            else
            {
                var path = ctx.Request.Path;
                if (!path.StartsWithSegments("/setup") && !path.StartsWithSegments("/css") &&
                    !path.StartsWithSegments("/health") && !path.StartsWithSegments("/favicon.svg"))
                {
                    if (path.StartsWithSegments("/dav") || path.StartsWithSegments("/.well-known"))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        await ctx.Response.WriteAsync("Server setup has not been completed.");
                        return;
                    }
                    ctx.Response.Redirect(ctx.Request.PathBase + "/setup");
                    return;
                }
            }
        }
        await next(ctx);
    }
}
