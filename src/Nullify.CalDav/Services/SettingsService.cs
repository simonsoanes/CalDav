using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

public sealed record ServerSettings
{
    public bool AllowAccountPasswordForDav { get; init; } = true;

    public bool OidcEnabled { get; init; }
    public string? OidcAuthority { get; init; }
    public string? OidcClientId { get; init; }
    public string? OidcClientSecret { get; init; }
    public string OidcScopes { get; init; } = "openid profile email";
    public string OidcUsernameClaim { get; init; } = "preferred_username";
    public string OidcButtonLabel { get; init; } = "Sign in with SSO";
    public bool OidcAutoProvision { get; init; }
    public bool OidcLinkByUsername { get; init; }
    public string? OidcAdminClaimType { get; init; }
    public string? OidcAdminClaimValue { get; init; }
    public bool OidcRequireHttpsMetadata { get; init; } = true;
    public bool HideLocalLogin { get; init; }

    /// <summary>Public base URL used in client autoconfiguration (e.g. https://dav.example.com).</summary>
    public string? PublicUrl { get; init; }

    /// <summary>Mail domains served by autoconfiguration, comma separated.</summary>
    public string? MailDomains { get; init; }
    public string? MailProviderName { get; init; }
    public string? ImapHost { get; init; }
    public int ImapPort { get; init; } = 993;
    public MailSecurity ImapSecurity { get; init; } = MailSecurity.Tls;
    public bool ImapAllowInvalidCertificates { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public MailSecurity SmtpSecurity { get; init; } = MailSecurity.StartTls;
    public bool AutoconfigEnabled { get; init; } = true;
    public bool SyncMlEnabled { get; init; } = true;

    /// <summary>Default per-user storage quota in megabytes (0 = unlimited).</summary>
    public long DefaultQuotaMb { get; init; }
    public int TrashRetentionDays { get; init; } = 30;
    public bool DirectoryEnabled { get; init; } = true;
    public bool BirthdaysEnabled { get; init; } = true;
    public bool SubscriptionsEnabled { get; init; } = true;
    public bool SubscriptionsAllowPrivateNetworks { get; init; }
    public int SubscriptionMinRefreshMinutes { get; init; } = 15;
    public bool PublicLinksEnabled { get; init; } = true;
    public bool PushEnabled { get; init; } = true;
    public string? VapidPublicKey { get; init; }
    public string? VapidPrivateKey { get; init; }
    public string? PushContact { get; init; }
    public bool SchedulingEnabled { get; init; } = true;
    public bool SendInvitationEmails { get; init; }
    public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public string? InvitationFrom { get; init; }

    public bool ImapConfigured => !string.IsNullOrWhiteSpace(ImapHost);
    public bool SmtpConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(InvitationFrom);

    public bool OidcConfigured =>
        OidcEnabled && !string.IsNullOrWhiteSpace(OidcAuthority) && !string.IsNullOrWhiteSpace(OidcClientId);
}

/// <summary>Database-backed server settings that can be changed from the admin UI at runtime.</summary>
public sealed class SettingsService(
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcCache,
    IConfiguration configuration)
{
    private readonly string? _configuredPublicUrl = string.IsNullOrWhiteSpace(configuration["PublicUrl"]) ? null : configuration["PublicUrl"]!.TrimEnd('/');
    private readonly IDataProtector _protector = protection.CreateProtector("Nullify.CalDav.Settings");
    private ServerSettings _current = new();

    public ServerSettings Current => _current;

    public void Load()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var values = db.Settings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        _current = FromDictionary(values);
    }

    public async Task SaveAsync(ServerSettings settings, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Settings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, value) in ToDictionary(settings))
        {
            if (existing.TryGetValue(key, out var row)) row.Value = value;
            else db.Settings.Add(new Setting { Key = key, Value = value });
        }
        await db.SaveChangesAsync(ct);
        _current = settings;
        oidcCache.TryRemove(OidcSetup.Scheme);
    }

    private Dictionary<string, string?> ToDictionary(ServerSettings s) => new()
    {
        ["Dav.AllowAccountPassword"] = s.AllowAccountPasswordForDav.ToString(),
        ["Oidc.Enabled"] = s.OidcEnabled.ToString(),
        ["Oidc.Authority"] = s.OidcAuthority,
        ["Oidc.ClientId"] = s.OidcClientId,
        ["Oidc.ClientSecret"] = string.IsNullOrEmpty(s.OidcClientSecret) ? null : _protector.Protect(s.OidcClientSecret),
        ["Oidc.Scopes"] = s.OidcScopes,
        ["Oidc.UsernameClaim"] = s.OidcUsernameClaim,
        ["Oidc.ButtonLabel"] = s.OidcButtonLabel,
        ["Oidc.AutoProvision"] = s.OidcAutoProvision.ToString(),
        ["Oidc.LinkByUsername"] = s.OidcLinkByUsername.ToString(),
        ["Oidc.AdminClaimType"] = s.OidcAdminClaimType,
        ["Oidc.AdminClaimValue"] = s.OidcAdminClaimValue,
        ["Oidc.RequireHttpsMetadata"] = s.OidcRequireHttpsMetadata.ToString(),
        ["Ui.HideLocalLogin"] = s.HideLocalLogin.ToString(),
        ["Server.PublicUrl"] = s.PublicUrl,
        ["Mail.Domains"] = s.MailDomains,
        ["Mail.ProviderName"] = s.MailProviderName,
        ["Mail.ImapHost"] = s.ImapHost,
        ["Mail.ImapPort"] = s.ImapPort.ToString(),
        ["Mail.ImapSecurity"] = s.ImapSecurity.ToString(),
        ["Mail.ImapAllowInvalidCertificates"] = s.ImapAllowInvalidCertificates.ToString(),
        ["Mail.SmtpHost"] = s.SmtpHost,
        ["Mail.SmtpPort"] = s.SmtpPort.ToString(),
        ["Mail.SmtpSecurity"] = s.SmtpSecurity.ToString(),
        ["Autoconfig.Enabled"] = s.AutoconfigEnabled.ToString(),
        ["SyncMl.Enabled"] = s.SyncMlEnabled.ToString(),
        ["Quota.DefaultMb"] = s.DefaultQuotaMb.ToString(),
        ["Trash.RetentionDays"] = s.TrashRetentionDays.ToString(),
        ["Directory.Enabled"] = s.DirectoryEnabled.ToString(),
        ["Birthdays.Enabled"] = s.BirthdaysEnabled.ToString(),
        ["Subscriptions.Enabled"] = s.SubscriptionsEnabled.ToString(),
        ["Subscriptions.AllowPrivate"] = s.SubscriptionsAllowPrivateNetworks.ToString(),
        ["Subscriptions.MinRefresh"] = s.SubscriptionMinRefreshMinutes.ToString(),
        ["PublicLinks.Enabled"] = s.PublicLinksEnabled.ToString(),
        ["Push.Enabled"] = s.PushEnabled.ToString(),
        ["Push.VapidPublicKey"] = s.VapidPublicKey,
        ["Push.VapidPrivateKey"] = string.IsNullOrEmpty(s.VapidPrivateKey) ? null : _protector.Protect(s.VapidPrivateKey),
        ["Push.Contact"] = s.PushContact,
        ["Scheduling.Enabled"] = s.SchedulingEnabled.ToString(),
        ["Scheduling.SendEmails"] = s.SendInvitationEmails.ToString(),
        ["Mail.SmtpUsername"] = s.SmtpUsername,
        ["Mail.SmtpPassword"] = string.IsNullOrEmpty(s.SmtpPassword) ? null : _protector.Protect(s.SmtpPassword),
        ["Mail.InvitationFrom"] = s.InvitationFrom,
    };

    private ServerSettings FromDictionary(Dictionary<string, string?> d)
    {
        string? S(string k) => d.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        bool B(string k, bool def) => bool.TryParse(S(k), out var b) ? b : def;
        int I(string k, int def) => int.TryParse(S(k), out var i) ? i : def;
        MailSecurity M(string k, MailSecurity def) => Enum.TryParse<MailSecurity>(S(k), out var m) ? m : def;

        string? secret = null;
        if (S("Oidc.ClientSecret") is { } protectedSecret)
        {
            try { secret = _protector.Unprotect(protectedSecret); }
            catch (System.Security.Cryptography.CryptographicException) { secret = null; }
        }

        string? Secret(string k)
        {
            if (S(k) is not { } p) return null;
            try { return _protector.Unprotect(p); }
            catch (System.Security.Cryptography.CryptographicException) { return null; }
        }

        var defaults = new ServerSettings();
        return new ServerSettings
        {
            AllowAccountPasswordForDav = B("Dav.AllowAccountPassword", defaults.AllowAccountPasswordForDav),
            OidcEnabled = B("Oidc.Enabled", false),
            OidcAuthority = S("Oidc.Authority"),
            OidcClientId = S("Oidc.ClientId"),
            OidcClientSecret = secret,
            OidcScopes = S("Oidc.Scopes") ?? defaults.OidcScopes,
            OidcUsernameClaim = S("Oidc.UsernameClaim") ?? defaults.OidcUsernameClaim,
            OidcButtonLabel = S("Oidc.ButtonLabel") ?? defaults.OidcButtonLabel,
            OidcAutoProvision = B("Oidc.AutoProvision", false),
            OidcLinkByUsername = B("Oidc.LinkByUsername", false),
            OidcAdminClaimType = S("Oidc.AdminClaimType"),
            OidcAdminClaimValue = S("Oidc.AdminClaimValue"),
            OidcRequireHttpsMetadata = B("Oidc.RequireHttpsMetadata", true),
            HideLocalLogin = B("Ui.HideLocalLogin", false),
            PublicUrl = S("Server.PublicUrl") ?? _configuredPublicUrl,
            MailDomains = S("Mail.Domains"),
            MailProviderName = S("Mail.ProviderName"),
            ImapHost = S("Mail.ImapHost"),
            ImapPort = I("Mail.ImapPort", defaults.ImapPort),
            ImapSecurity = M("Mail.ImapSecurity", defaults.ImapSecurity),
            ImapAllowInvalidCertificates = B("Mail.ImapAllowInvalidCertificates", false),
            SmtpHost = S("Mail.SmtpHost"),
            SmtpPort = I("Mail.SmtpPort", defaults.SmtpPort),
            SmtpSecurity = M("Mail.SmtpSecurity", defaults.SmtpSecurity),
            AutoconfigEnabled = B("Autoconfig.Enabled", true),
            SyncMlEnabled = B("SyncMl.Enabled", true),
            DefaultQuotaMb = long.TryParse(S("Quota.DefaultMb"), out var q) ? Math.Max(0, q) : 0,
            TrashRetentionDays = Math.Clamp(I("Trash.RetentionDays", defaults.TrashRetentionDays), 0, 3650),
            DirectoryEnabled = B("Directory.Enabled", true),
            BirthdaysEnabled = B("Birthdays.Enabled", true),
            SubscriptionsEnabled = B("Subscriptions.Enabled", true),
            SubscriptionsAllowPrivateNetworks = B("Subscriptions.AllowPrivate", false),
            SubscriptionMinRefreshMinutes = Math.Max(1, I("Subscriptions.MinRefresh", defaults.SubscriptionMinRefreshMinutes)),
            PublicLinksEnabled = B("PublicLinks.Enabled", true),
            PushEnabled = B("Push.Enabled", true),
            VapidPublicKey = S("Push.VapidPublicKey"),
            VapidPrivateKey = Secret("Push.VapidPrivateKey"),
            PushContact = S("Push.Contact"),
            SchedulingEnabled = B("Scheduling.Enabled", true),
            SendInvitationEmails = B("Scheduling.SendEmails", false),
            SmtpUsername = S("Mail.SmtpUsername"),
            SmtpPassword = Secret("Mail.SmtpPassword"),
            InvitationFrom = S("Mail.InvitationFrom"),
        };
    }
}

/// <summary>Serialises all mutating storage operations; SQLite only supports a single writer.</summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) s.Release();
        }
    }
}
