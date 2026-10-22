// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

public static class AppClaims
{
    public const string Stamp = "nullify:stamp";
    public const string AdminRole = "admin";
    public const string AdminPolicy = "Admin";
}

public sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public const int MinimumLength = 8;

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>Verifies the password; upgrades the stored hash in place when the algorithm parameters changed.</summary>
    public bool Verify(User user, string password, out bool rehashed)
    {
        rehashed = false;
        if (string.IsNullOrEmpty(user.PasswordHash)) return false;
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            rehashed = true;
        }
        return result != PasswordVerificationResult.Failed;
    }

    public static string? ValidateStrength(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return $"Password must be at least {MinimumLength} characters.";
        return null;
    }

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Generates an app password such as "abcd-efgh-ijkl-mnop-qrst-uvwx".</summary>
    public static string GenerateToken()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[29];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (i + 1) % 5 == 0 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}

/// <summary>Simple in-memory brute force protection keyed by client address.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (int Count, DateTime First)> _failures = new();

    public bool IsBlocked(string key) =>
        _failures.TryGetValue(key, out var e) && e.Count >= MaxFailures && DateTime.UtcNow - e.First < Window;

    public void RecordFailure(string key)
    {
        var now = DateTime.UtcNow;
        _failures.AddOrUpdate(key, (1, now), (_, e) => now - e.First > Window ? (1, now) : (e.Count + 1, e.First));
        if (_failures.Count > 10_000)
            foreach (var kv in _failures.Where(kv => now - kv.Value.First > Window).ToList())
                _failures.TryRemove(kv.Key, out _);
    }

    public void Reset(string key) => _failures.TryRemove(key, out _);
}

/// <summary>
/// Validates HTTP Basic credentials for DAV and SyncML clients. Accepts app passwords and (optionally)
/// the account password, which may be checked against an IMAP server. Successful validations are cached
/// briefly because DAV clients authenticate every request and password checks are deliberately slow.
/// </summary>
public sealed class DavCredentialValidator(
    CredentialService credentials, LoginThrottle throttle, SettingsService settings)
{
    private readonly ConcurrentDictionary<string, (int UserId, DateTime Expires)> _cache = new();
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    public void InvalidateAll() => _cache.Clear();

    public async Task<User?> ValidateAsync(AppDbContext db, string username, string password, string clientKey, CancellationToken ct)
    {
        var cacheKey = PasswordService.Sha256(username.ToLowerInvariant() + "\0" + password);
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            var cached = await db.Users.FirstOrDefaultAsync(u => u.Id == hit.UserId, ct);
            if (cached is { IsEnabled: true }) return cached;
            _cache.TryRemove(cacheKey, out _);
        }

        if (throttle.IsBlocked(clientKey)) return null;

        var user = await credentials.FindByLoginAsync(db, username, ct);
        if (user is not { IsEnabled: true })
        {
            throttle.RecordFailure(clientKey);
            return null;
        }

        var tokenHash = PasswordService.Sha256(password.Trim());
        var app = await db.AppPasswords.FirstOrDefaultAsync(a => a.UserId == user.Id && a.TokenHash == tokenHash, ct);
        var ok = false;
        if (app != null)
        {
            ok = true;
            app.LastUsedUtc = DateTime.UtcNow;
        }
        else if (settings.Current.AllowAccountPasswordForDav && await credentials.VerifyPasswordAsync(user, password, ct))
        {
            ok = true;
        }

        if (!ok)
        {
            throttle.RecordFailure(clientKey);
            return null;
        }

        throttle.Reset(clientKey);
        await db.SaveChangesAsync(ct);
        _cache[cacheKey] = (user.Id, DateTime.UtcNow + CacheTime);
        return user;
    }
}

public static class PrincipalFactory
{
    public static ClaimsPrincipal Create(User user, string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("display_name", string.IsNullOrEmpty(user.DisplayName) ? user.Username : user.DisplayName),
            new(AppClaims.Stamp, user.SecurityStamp),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, AppClaims.AdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static int? UserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
