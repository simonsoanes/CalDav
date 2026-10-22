using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.Admin;

public class SettingsModel(SettingsService settings, DavCredentialValidator credentials, ImapAuthenticator imap, SmtpSender smtp,
    SystemCollectionsService system, BirthdayService birthdays, Nullify.CalDav.Data.AppDbContext db) : AppPageModel
{
    [BindProperty, Display(Name = "Allow account passwords for CalDAV/CardDAV clients")] public bool AllowAccountPasswordForDav { get; set; }
    [BindProperty, Display(Name = "Enable OpenID Connect single sign-on")] public bool OidcEnabled { get; set; }
    [BindProperty, Display(Name = "Authority (issuer URL)")] public string? OidcAuthority { get; set; }
    [BindProperty, Display(Name = "Client ID")] public string? OidcClientId { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "Client secret")] public string? OidcClientSecret { get; set; }
    [BindProperty, Display(Name = "Remove stored client secret")] public bool ClearSecret { get; set; }
    [BindProperty, Display(Name = "Scopes")] public string OidcScopes { get; set; } = "";
    [BindProperty, Display(Name = "Username claim")] public string OidcUsernameClaim { get; set; } = "";
    [BindProperty, Display(Name = "Button label")] public string OidcButtonLabel { get; set; } = "";
    [BindProperty, Display(Name = "Automatically create accounts for new SSO users")] public bool OidcAutoProvision { get; set; }
    [BindProperty, Display(Name = "Link SSO identities to existing accounts with the same username")] public bool OidcLinkByUsername { get; set; }
    [BindProperty, Display(Name = "Admin claim type")] public string? OidcAdminClaimType { get; set; }
    [BindProperty, Display(Name = "Admin claim value")] public string? OidcAdminClaimValue { get; set; }
    [BindProperty, Display(Name = "Require HTTPS metadata")] public bool OidcRequireHttpsMetadata { get; set; }
    [BindProperty, Display(Name = "Hide the password form on the sign-in page")] public bool HideLocalLogin { get; set; }

    [BindProperty, Display(Name = "Public server URL")] public string? PublicUrl { get; set; }
    [BindProperty, Display(Name = "Mail domains")] public string? MailDomains { get; set; }
    [BindProperty, Display(Name = "Provider name")] public string? MailProviderName { get; set; }
    [BindProperty, Display(Name = "IMAP server")] public string? ImapHost { get; set; }
    [BindProperty, Display(Name = "IMAP port")] public int ImapPort { get; set; } = 993;
    [BindProperty, Display(Name = "IMAP security")] public MailSecurity ImapSecurity { get; set; }
    [BindProperty, Display(Name = "Accept invalid IMAP TLS certificates (passthrough only)")] public bool ImapAllowInvalidCertificates { get; set; }
    [BindProperty, Display(Name = "SMTP server")] public string? SmtpHost { get; set; }
    [BindProperty, Display(Name = "SMTP port")] public int SmtpPort { get; set; } = 587;
    [BindProperty, Display(Name = "SMTP security")] public MailSecurity SmtpSecurity { get; set; } = MailSecurity.StartTls;
    [BindProperty, Display(Name = "Publish client autoconfiguration (Thunderbird autoconfig, Outlook Autodiscover)")] public bool AutoconfigEnabled { get; set; }
    [BindProperty, Display(Name = "Enable SyncML")] public bool SyncMlEnabled { get; set; }

    [BindProperty, Display(Name = "Default storage quota per user (MB, 0 = unlimited)")] public long DefaultQuotaMb { get; set; }
    [BindProperty, Display(Name = "Keep deleted items for (days)")] public int TrashRetentionDays { get; set; } = 30;
    [BindProperty, Display(Name = "Give every user a read-only directory address book of all users")] public bool DirectoryEnabled { get; set; }
    [BindProperty, Display(Name = "Give every user a birthday calendar generated from their contacts")] public bool BirthdaysEnabled { get; set; }
    [BindProperty, Display(Name = "Allow calendar subscriptions (webcal feeds)")] public bool SubscriptionsEnabled { get; set; }
    [BindProperty, Display(Name = "Allow outgoing requests to private networks (subscriptions, push)")] public bool SubscriptionsAllowPrivateNetworks { get; set; }
    [BindProperty, Display(Name = "Minimum subscription refresh interval (minutes)")] public int SubscriptionMinRefreshMinutes { get; set; } = 15;
    [BindProperty, Display(Name = "Allow public calendar links")] public bool PublicLinksEnabled { get; set; }
    [BindProperty, Display(Name = "Enable WebDAV-Push (instant sync for DAVx⁵)")] public bool PushEnabled { get; set; }
    [BindProperty, Display(Name = "Push contact (email for push services)")] public string? PushContact { get; set; }
    [BindProperty, Display(Name = "Process meeting invitations on the server (RFC 6638)")] public bool SchedulingEnabled { get; set; }
    [BindProperty, Display(Name = "Email invitations to people outside this server (iMIP)")] public bool SendInvitationEmails { get; set; }
    [BindProperty, Display(Name = "Invitation sender address")] public string? InvitationFrom { get; set; }
    [BindProperty, Display(Name = "SMTP username")] public string? SmtpUsername { get; set; }
    [BindProperty, DataType(DataType.Password), Display(Name = "SMTP password")] public string? SmtpPassword { get; set; }
    [BindProperty, Display(Name = "Remove stored SMTP password")] public bool ClearSmtpPassword { get; set; }
    [BindProperty, Display(Name = "Test recipient")] public string? TestRecipient { get; set; }

    public bool HasSmtpPassword { get; private set; }
    public bool HasSecret { get; private set; }


    public string EffectiveBaseUrl => Autoconfig.PublicBaseUrl(Request, settings.Current);
    public string CallbackUrl => ServerUrl + "/signin-oidc";

    public void OnGet()
    {
        var s = settings.Current;
        AllowAccountPasswordForDav = s.AllowAccountPasswordForDav;
        OidcEnabled = s.OidcEnabled;
        OidcAuthority = s.OidcAuthority;
        OidcClientId = s.OidcClientId;
        HasSecret = !string.IsNullOrEmpty(s.OidcClientSecret);
        OidcScopes = s.OidcScopes;
        OidcUsernameClaim = s.OidcUsernameClaim;
        OidcButtonLabel = s.OidcButtonLabel;
        OidcAutoProvision = s.OidcAutoProvision;
        OidcLinkByUsername = s.OidcLinkByUsername;
        OidcAdminClaimType = s.OidcAdminClaimType;
        OidcAdminClaimValue = s.OidcAdminClaimValue;
        OidcRequireHttpsMetadata = s.OidcRequireHttpsMetadata;
        HideLocalLogin = s.HideLocalLogin;
        PublicUrl = s.PublicUrl;
        MailDomains = s.MailDomains;
        MailProviderName = s.MailProviderName;
        ImapHost = s.ImapHost;
        ImapPort = s.ImapPort;
        ImapSecurity = s.ImapSecurity;
        ImapAllowInvalidCertificates = s.ImapAllowInvalidCertificates;
        SmtpHost = s.SmtpHost;
        SmtpPort = s.SmtpPort;
        SmtpSecurity = s.SmtpSecurity;
        AutoconfigEnabled = s.AutoconfigEnabled;
        SyncMlEnabled = s.SyncMlEnabled;
        DefaultQuotaMb = s.DefaultQuotaMb;
        TrashRetentionDays = s.TrashRetentionDays;
        DirectoryEnabled = s.DirectoryEnabled;
        BirthdaysEnabled = s.BirthdaysEnabled;
        SubscriptionsEnabled = s.SubscriptionsEnabled;
        SubscriptionsAllowPrivateNetworks = s.SubscriptionsAllowPrivateNetworks;
        SubscriptionMinRefreshMinutes = s.SubscriptionMinRefreshMinutes;
        PublicLinksEnabled = s.PublicLinksEnabled;
        PushEnabled = s.PushEnabled;
        PushContact = s.PushContact;
        SchedulingEnabled = s.SchedulingEnabled;
        SendInvitationEmails = s.SendInvitationEmails;
        InvitationFrom = s.InvitationFrom;
        SmtpUsername = s.SmtpUsername;
        HasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPassword);
    }

    public async Task<IActionResult> OnPostTestSmtpAsync()
    {
        var s = settings.Current;
        if (!s.SmtpConfigured || string.IsNullOrWhiteSpace(TestRecipient))
        {
            ErrorMessage = "Save an SMTP server and sender address first, and enter a test recipient.";
            return RedirectToPage();
        }
        try
        {
            var body = "From: <" + s.InvitationFrom + ">\r\nTo: <" + TestRecipient.Trim() + ">\r\nSubject: Nullify.CalDav test message\r\n" +
                       "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSMTP settings work.\r\n";
            await smtp.SendAsync(new OutgoingMail(s.InvitationFrom!, null, TestRecipient.Trim(), "Nullify.CalDav test message", body), HttpContext.RequestAborted);
            StatusMessage = "Test message sent to " + TestRecipient.Trim() + ".";
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or InvalidOperationException or OperationCanceledException)
        {
            ErrorMessage = "Sending failed: " + ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestImapAsync()
    {
        OnGet();
        if (!settings.Current.ImapConfigured)
        {
            ErrorMessage = "Save an IMAP server first.";
            return RedirectToPage();
        }
        var s = settings.Current;
        var (ok, message) = await imap.ProbeAsync(new ImapEndpoint(s.ImapHost!, s.ImapPort, s.ImapSecurity, s.ImapAllowInvalidCertificates), HttpContext.RequestAborted);
        if (ok) StatusMessage = message; else ErrorMessage = message;
        return RedirectToPage();
    }

    private static bool ValidHost(string? host) =>
        string.IsNullOrWhiteSpace(host) || (!host.Contains('/') && !host.Contains(' ') && Uri.CheckHostName(host.Trim()) != UriHostNameType.Unknown);

    public async Task<IActionResult> OnPostAsync()
    {
        var current = settings.Current;
        HasSecret = !string.IsNullOrEmpty(current.OidcClientSecret);

        if (OidcEnabled)
        {
            if (!Uri.TryCreate(OidcAuthority?.Trim(), UriKind.Absolute, out var authority) || (authority.Scheme != "https" && authority.Scheme != "http"))
                ModelState.AddModelError(nameof(OidcAuthority), "Enter the issuer URL of your identity provider.");
            else if (authority.Scheme == "http" && OidcRequireHttpsMetadata)
                ModelState.AddModelError(nameof(OidcAuthority), "The authority must use HTTPS unless 'Require HTTPS metadata' is disabled.");
            if (string.IsNullOrWhiteSpace(OidcClientId))
                ModelState.AddModelError(nameof(OidcClientId), "A client ID is required.");
        }
        if (!string.IsNullOrWhiteSpace(PublicUrl) &&
            (!Uri.TryCreate(PublicUrl.Trim(), UriKind.Absolute, out var pub) || pub.Scheme is not ("http" or "https") || pub.Query.Length > 0))
            ModelState.AddModelError(nameof(PublicUrl), "Enter an absolute URL such as https://dav.example.com");
        if (!ValidHost(ImapHost)) ModelState.AddModelError(nameof(ImapHost), "Enter a host name or IP address.");
        if (!ValidHost(SmtpHost)) ModelState.AddModelError(nameof(SmtpHost), "Enter a host name or IP address.");
        if (ImapPort is < 1 or > 65535) ModelState.AddModelError(nameof(ImapPort), "Invalid port.");
        if (SmtpPort is < 1 or > 65535) ModelState.AddModelError(nameof(SmtpPort), "Invalid port.");
        if (SendInvitationEmails && (string.IsNullOrWhiteSpace(SmtpHost) || string.IsNullOrWhiteSpace(InvitationFrom)))
            ModelState.AddModelError(nameof(SendInvitationEmails), "Invitation emails need an SMTP server and a sender address.");
        if (!string.IsNullOrWhiteSpace(InvitationFrom) && !System.Net.Mail.MailAddress.TryCreate(InvitationFrom.Trim(), out _))
            ModelState.AddModelError(nameof(InvitationFrom), "Enter a valid email address.");
        if (HideLocalLogin && !OidcEnabled)
            ModelState.AddModelError(nameof(HideLocalLogin), "The password form can only be hidden while single sign-on is enabled.");
        if (!ModelState.IsValid) return Page();

        var secret = ClearSecret ? null : string.IsNullOrEmpty(OidcClientSecret) ? current.OidcClientSecret : OidcClientSecret;
        await settings.SaveAsync(new ServerSettings
        {
            AllowAccountPasswordForDav = AllowAccountPasswordForDav,
            OidcEnabled = OidcEnabled,
            OidcAuthority = OidcAuthority?.Trim(),
            OidcClientId = OidcClientId?.Trim(),
            OidcClientSecret = secret,
            OidcScopes = string.IsNullOrWhiteSpace(OidcScopes) ? "openid profile email" : OidcScopes.Trim(),
            OidcUsernameClaim = string.IsNullOrWhiteSpace(OidcUsernameClaim) ? "preferred_username" : OidcUsernameClaim.Trim(),
            OidcButtonLabel = string.IsNullOrWhiteSpace(OidcButtonLabel) ? "Sign in with SSO" : OidcButtonLabel.Trim(),
            OidcAutoProvision = OidcAutoProvision,
            OidcLinkByUsername = OidcLinkByUsername,
            OidcAdminClaimType = string.IsNullOrWhiteSpace(OidcAdminClaimType) ? null : OidcAdminClaimType.Trim(),
            OidcAdminClaimValue = string.IsNullOrWhiteSpace(OidcAdminClaimValue) ? null : OidcAdminClaimValue.Trim(),
            OidcRequireHttpsMetadata = OidcRequireHttpsMetadata,
            HideLocalLogin = HideLocalLogin,
            PublicUrl = string.IsNullOrWhiteSpace(PublicUrl) ? null : PublicUrl.Trim().TrimEnd('/'),
            MailDomains = string.IsNullOrWhiteSpace(MailDomains) ? null
                : string.Join(',', MailDomains.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().ToLowerInvariant())),
            MailProviderName = string.IsNullOrWhiteSpace(MailProviderName) ? null : MailProviderName.Trim(),
            ImapHost = string.IsNullOrWhiteSpace(ImapHost) ? null : ImapHost.Trim(),
            ImapPort = ImapPort,
            ImapSecurity = ImapSecurity,
            ImapAllowInvalidCertificates = ImapAllowInvalidCertificates,
            SmtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? null : SmtpHost.Trim(),
            SmtpPort = SmtpPort,
            SmtpSecurity = SmtpSecurity,
            AutoconfigEnabled = AutoconfigEnabled,
            SyncMlEnabled = SyncMlEnabled,
            DefaultQuotaMb = Math.Max(0, DefaultQuotaMb),
            TrashRetentionDays = Math.Clamp(TrashRetentionDays, 0, 3650),
            DirectoryEnabled = DirectoryEnabled,
            BirthdaysEnabled = BirthdaysEnabled,
            SubscriptionsEnabled = SubscriptionsEnabled,
            SubscriptionsAllowPrivateNetworks = SubscriptionsAllowPrivateNetworks,
            SubscriptionMinRefreshMinutes = Math.Max(1, SubscriptionMinRefreshMinutes),
            PublicLinksEnabled = PublicLinksEnabled,
            PushEnabled = PushEnabled,
            PushContact = string.IsNullOrWhiteSpace(PushContact) ? null : PushContact.Trim(),
            VapidPublicKey = current.VapidPublicKey,
            VapidPrivateKey = current.VapidPrivateKey,
            SchedulingEnabled = SchedulingEnabled,
            SendInvitationEmails = SendInvitationEmails,
            InvitationFrom = string.IsNullOrWhiteSpace(InvitationFrom) ? null : InvitationFrom.Trim(),
            SmtpUsername = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername.Trim(),
            SmtpPassword = ClearSmtpPassword ? null : string.IsNullOrEmpty(SmtpPassword) ? current.SmtpPassword : SmtpPassword,
        });
        if (current.DirectoryEnabled != DirectoryEnabled || current.BirthdaysEnabled != BirthdaysEnabled)
        {
            await system.EnsureAllAsync(HttpContext.RequestAborted);
            system.RequestDirectoryRebuild();
            if (BirthdaysEnabled)
                foreach (var id in db.Users.Select(u => u.Id).ToList())
                    await birthdays.RebuildAsync(id, HttpContext.RequestAborted);
        }
        credentials.InvalidateAll();
        StatusMessage = "Settings saved.";
        return RedirectToPage();
    }
}
