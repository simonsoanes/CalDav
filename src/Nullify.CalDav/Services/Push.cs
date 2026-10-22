// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

/// <summary>
/// WebDAV-Push (https://github.com/bitfireAT/webdav-push) using Web Push (RFC 8030) with message encryption
/// (RFC 8291, aes128gcm) and VAPID (RFC 8292). Supported by DAVx⁵ via UnifiedPush.
/// </summary>
public sealed class PushService(SettingsService settings, IServiceScopeFactory scopes, OutboundHttp http, ILogger<PushService> log)
{
    public static readonly XNamespace P = "https://bitfire.at/webdav-push";
    public static readonly TimeSpan MaxRegistrationLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultRegistrationLifetime = TimeSpan.FromDays(3);
    private readonly Lock _keyLock = new();
    private ECDsa? _vapid;
    private byte[]? _topicKey;

    // ------------------------------------------------------------------ Keys and topics

    private ECDsa Vapid
    {
        get
        {
            lock (_keyLock)
            {
                if (_vapid != null) return _vapid;
                var s = settings.Current;
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                if (!string.IsNullOrEmpty(s.VapidPrivateKey))
                {
                    try { key.ImportECPrivateKey(Convert.FromBase64String(s.VapidPrivateKey), out _); }
                    catch (CryptographicException) { key = ECDsa.Create(ECCurve.NamedCurves.nistP256); s = s with { VapidPrivateKey = null }; }
                }
                if (string.IsNullOrEmpty(s.VapidPrivateKey))
                {
                    var publicKey = Base64Url(PublicPoint(key.ExportParameters(false)));
                    settings.SaveAsync(s with { VapidPrivateKey = Convert.ToBase64String(key.ExportECPrivateKey()), VapidPublicKey = publicKey })
                        .GetAwaiter().GetResult();
                }
                _topicKey = SHA256.HashData(key.ExportECPrivateKey());
                _vapid = key;
                return key;
            }
        }
    }

    public string VapidPublicKey => Base64Url(PublicPoint(Vapid.ExportParameters(false)));

    /// <summary>Opaque, stable topic identifying a collection.</summary>
    public string Topic(string type, int collectionId)
    {
        _ = Vapid;
        var mac = HMACSHA256.HashData(_topicKey!, Encoding.UTF8.GetBytes($"{type}:{collectionId}"));
        return Base64Url(mac)[..22];
    }

    private static byte[] PublicPoint(ECParameters p) => [0x04, .. p.Q.X!, .. p.Q.Y!];

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // ------------------------------------------------------------------ Registration

    public sealed record RegisterResult(int Status, string? RegistrationId, DateTime Expires);

    /// <summary>Handles a push-register request body for a collection.</summary>
    public async Task<RegisterResult> RegisterAsync(AppDbContext db, int userId, string type, int collectionId, XElement body, CancellationToken ct)
    {
        var sub = body.Element(P + "subscription")?.Element(P + "web-push-subscription")
                  ?? throw new FormatException("Only web-push subscriptions are supported");
        var resource = sub.Element(P + "push-resource")?.Value.Trim();
        if (string.IsNullOrEmpty(resource) || !Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || resourceUri.Scheme is not ("https" or "http"))
            throw new FormatException("A valid push-resource is required");
        var publicKey = (sub.Element(P + "subscription-public-key") ?? sub.Element(P + "client-public-key"))?.Value.Trim();
        var auth = sub.Element(P + "auth-secret")?.Value.Trim();
        if (publicKey != null && auth != null)
        {
            // Validate the keys early so delivery cannot fail later.
            if (FromBase64Url(publicKey).Length != 65 || FromBase64Url(auth).Length < 16) throw new FormatException("Invalid subscription keys");
        }

        var trigger = body.Element(P + "trigger");
        var contentUpdate = trigger == null || trigger.Element(P + "content-update") != null;
        var propertyUpdate = trigger?.Element(P + "property-update") != null;

        var expires = DateTime.UtcNow + DefaultRegistrationLifetime;
        var requested = body.Element(XName.Get("expires", "DAV:"))?.Value;
        if (requested != null && DateTime.TryParse(requested, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var req))
            expires = req;
        var max = DateTime.UtcNow + MaxRegistrationLifetime;
        if (expires > max) expires = max;

        var existing = await db.PushRegistrations.FirstOrDefaultAsync(r =>
            r.UserId == userId && r.CollectionType == type && r.CollectionId == collectionId && r.PushResource == resource, ct);
        var created = existing == null;
        existing ??= new PushRegistration { UserId = userId, CollectionType = type, CollectionId = collectionId, PushResource = resource };
        existing.PublicKey = publicKey;
        existing.AuthSecret = auth;
        existing.ContentUpdate = contentUpdate;
        existing.PropertyUpdate = propertyUpdate;
        existing.ExpiresUtc = expires;
        if (created) db.PushRegistrations.Add(existing);
        await db.SaveChangesAsync(ct);
        return new RegisterResult(created ? 201 : 204, existing.Id, expires);
    }

    // ------------------------------------------------------------------ Delivery

    public async Task DispatchAsync(CollectionChange change, CancellationToken ct)
    {
        if (!settings.Current.PushEnabled) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var regs = await db.PushRegistrations.Where(r => r.CollectionType == change.Type && r.CollectionId == change.CollectionId).ToListAsync(ct);
        if (regs.Count == 0) return;

        long? token = change.Type == CollectionChange.Calendar
            ? await db.Calendars.Where(c => c.Id == change.CollectionId).Select(c => (long?)c.SyncToken).FirstOrDefaultAsync(ct)
            : await db.AddressBooks.Where(b => b.Id == change.CollectionId).Select(b => (long?)b.SyncToken).FirstOrDefaultAsync(ct);
        if (token == null)
        {
            db.PushRegistrations.RemoveRange(regs); // collection deleted
            await db.SaveChangesAsync(ct);
            return;
        }

        var topic = Topic(change.Type, change.CollectionId);
        var message = new XElement(P + "push-message",
            new XAttribute(XNamespace.Xmlns + "P", P.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "D", "DAV:"),
            new XElement(P + "topic", topic),
            change.Content
                ? new XElement(P + "content-update", new XElement(XName.Get("sync-token", "DAV:"), Dav.DavPropertyProvider.FormatSyncToken(token.Value)))
                : new XElement(P + "property-update"));
        var payload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + message.ToString(SaveOptions.DisableFormatting));

        foreach (var reg in regs)
        {
            if (reg.ExpiresUtc < now) { db.PushRegistrations.Remove(reg); continue; }
            if (change.Content ? !reg.ContentUpdate : !reg.PropertyUpdate) continue;
            var status = await SendAsync(reg, payload, topic, ct);
            if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                log.LogInformation("Push subscription {Id} is gone; removing it", reg.Id);
                db.PushRegistrations.Remove(reg);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends one Web Push message. Returns the push service's status code (or null on network failure).</summary>
    public async Task<HttpStatusCode?> SendAsync(PushRegistration reg, byte[] payload, string? topic, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(reg.PushResource);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            byte[] body;
            if (!string.IsNullOrEmpty(reg.PublicKey) && !string.IsNullOrEmpty(reg.AuthSecret))
            {
                body = Encrypt(payload, FromBase64Url(reg.PublicKey), FromBase64Url(reg.AuthSecret));
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
            }
            else
            {
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            }
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Headers.TryAddWithoutValidation("Urgency", "normal");
            if (topic != null) request.Headers.TryAddWithoutValidation("Topic", topic);
            request.Headers.TryAddWithoutValidation("Authorization", VapidAuthorization(uri));

            using var response = await http.Client(settings.Current.SubscriptionsAllowPrivateNetworks).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                log.LogInformation("Push to {Host} failed with {Status}", uri.Host, (int)response.StatusCode);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or FormatException)
        {
            log.LogInformation("Push delivery failed: {Message}", ex.Message);
            return null;
        }
    }

    private string VapidAuthorization(Uri endpoint)
    {
        var contact = settings.Current.PushContact;
        if (string.IsNullOrWhiteSpace(contact)) contact = "mailto:admin@" + (settings.Current.MailDomains?.Split(',')[0].Trim() is { Length: > 0 } d ? d : "localhost");
        else if (!contact.StartsWith("mailto:") && !contact.StartsWith("https:")) contact = "mailto:" + contact;

        var header = Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = contact,
        }));
        var signingInput = header + "." + claims;
        var signature = Vapid.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={signingInput}.{Base64Url(signature)}, k={VapidPublicKey}";
    }

    /// <summary>RFC 8291 message encryption with the aes128gcm content coding (RFC 8188), single record.</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? ephemeral = null, byte[]? salt = null)
    {
        using var asKey = ephemeral ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = PublicPoint(asKey.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret);
        byte[] keyInfo = [.. "WebPush: info"u8, 0, .. uaPublic, .. asPublic];
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        salt ??= RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, [.. "Content-Encoding: aes128gcm"u8, 0]);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, [.. "Content-Encoding: nonce"u8, 0]);

        byte[] padded = [.. plaintext, 0x02]; // last record delimiter
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt (16) | record size (uint32 BE, 4096) | key id length | key id (sender public key).
        byte[] header = [.. salt, 0x00, 0x00, 0x10, 0x00, (byte)asPublic.Length, .. asPublic];
        return [.. header, .. ciphertext, .. tag];
    }
}

/// <summary>Consumes collection changes and fans them out to push subscribers and generated collections.</summary>
public sealed class ChangeDispatcher(CollectionChangeNotifier notifier, PushService push, BirthdayService birthdays, ILogger<ChangeDispatcher> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = notifier.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            // Coalesce bursts (e.g. a client uploading many items) into one notification per collection.
            await Task.Delay(TimeSpan.FromMilliseconds(750), stoppingToken);
            var batch = new HashSet<CollectionChange>();
            while (reader.TryRead(out var change)) batch.Add(change);
            foreach (var change in batch)
            {
                try
                {
                    await push.DispatchAsync(change, stoppingToken);
                    if (change.Content && change.Type == CollectionChange.AddressBook)
                        await birthdays.OnAddressBookChangedAsync(change.CollectionId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Processing change for {Type} {Id} failed", change.Type, change.CollectionId);
                }
            }
        }
    }
}
