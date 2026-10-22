// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

public sealed record ImapEndpoint(string Host, int Port, MailSecurity Security, bool AllowInvalidCertificates);

public enum ImapResult
{
    Success,
    InvalidCredentials,
    Unavailable,
}

/// <summary>
/// Minimal IMAP4rev1 client used to verify a user's credentials by attempting a login
/// (RFC 3501, STARTTLS per RFC 2595, AUTHENTICATE PLAIN per RFC 4616).
/// </summary>
public sealed class ImapAuthenticator(ILogger<ImapAuthenticator> log)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ImapResult> AuthenticateAsync(ImapEndpoint endpoint, string login, string password, CancellationToken ct)
    {
        if (password.Length == 0 || password.Contains('\0') || login.Contains('\0')) return ImapResult.InvalidCredentials;
        try
        {
            await using var session = await ImapSession.ConnectAsync(endpoint, ct);
            var result = await session.LoginAsync(login, password);
            await session.LogoutAsync();
            if (result == ImapResult.InvalidCredentials)
                log.LogInformation("IMAP server {Host} rejected credentials for {Login}", endpoint.Host, login);
            return result;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TlsException or TimeoutException or OperationCanceledException or ImapProtocolException)
        {
            log.LogWarning("IMAP authentication against {Host}:{Port} failed: {Message}", endpoint.Host, endpoint.Port, ex.Message);
            return ImapResult.Unavailable;
        }
    }

    /// <summary>Connects (and negotiates TLS) without logging in. Returns the advertised capabilities.</summary>
    public async Task<(bool Ok, string Message)> ProbeAsync(ImapEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await using var session = await ImapSession.ConnectAsync(endpoint, ct);
            var caps = string.Join(' ', session.Capabilities);
            await session.LogoutAsync();
            return (true, $"Connected to {endpoint.Host}:{endpoint.Port}. Capabilities: {caps}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TlsException or TimeoutException or OperationCanceledException or ImapProtocolException)
        {
            return (false, $"Could not connect to {endpoint.Host}:{endpoint.Port}: {ex.Message}");
        }
    }

    public sealed class ImapProtocolException(string message) : Exception(message);

    private sealed class TlsException(string message) : Exception(message);

    private sealed class ImapSession : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly CancellationTokenSource _cts;
        private Stream _stream;
        private int _tag;
        private readonly byte[] _buffer = new byte[4096];
        private int _bufferPos;
        private int _bufferLen;

        public HashSet<string> Capabilities { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private ImapSession(TcpClient tcp, Stream stream, CancellationTokenSource cts)
        {
            _tcp = tcp;
            _stream = stream;
            _cts = cts;
        }

        private CancellationToken Ct => _cts.Token;

        public static async Task<ImapSession> ConnectAsync(ImapEndpoint ep, CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(ep.Host, ep.Port, cts.Token);
                Stream stream = tcp.GetStream();
                if (ep.Security == MailSecurity.Tls) stream = await UpgradeAsync(stream, ep, cts.Token);

                var session = new ImapSession(tcp, stream, cts);
                var greeting = await session.ReadLineAsync();
                if (greeting.StartsWith("* BYE", StringComparison.OrdinalIgnoreCase))
                    throw new ImapProtocolException("Server refused the connection: " + greeting);
                if (!greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase))
                    throw new ImapProtocolException("Unexpected greeting: " + greeting);

                await session.RefreshCapabilitiesAsync();
                if (ep.Security == MailSecurity.StartTls)
                {
                    if (!session.Capabilities.Contains("STARTTLS"))
                        throw new ImapProtocolException("Server does not offer STARTTLS");
                    var (status, text) = await session.CommandAsync("STARTTLS");
                    if (status != "OK") throw new ImapProtocolException("STARTTLS failed: " + text);
                    session._stream = await UpgradeAsync(session._stream, ep, cts.Token);
                    session._bufferLen = session._bufferPos = 0;
                    await session.RefreshCapabilitiesAsync();
                }
                return session;
            }
            catch
            {
                tcp.Dispose();
                cts.Dispose();
                throw;
            }
        }

        private static async Task<Stream> UpgradeAsync(Stream inner, ImapEndpoint ep, CancellationToken ct)
        {
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false,
                (_, _, _, errors) => errors == SslPolicyErrors.None || ep.AllowInvalidCertificates);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ep.Host }, ct);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                throw new TlsException("TLS negotiation failed: " + ex.Message);
            }
            return ssl;
        }

        private async Task RefreshCapabilitiesAsync()
        {
            var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (status, _) = await CommandAsync("CAPABILITY", untagged: line =>
            {
                if (line.StartsWith("* CAPABILITY ", StringComparison.OrdinalIgnoreCase))
                    foreach (var c in line[13..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) caps.Add(c);
            });
            if (status != "OK") throw new ImapProtocolException("CAPABILITY failed");
            Capabilities = caps;
        }

        public async Task<ImapResult> LoginAsync(string login, string password)
        {
            string status;
            if (Capabilities.Contains("AUTH=PLAIN"))
            {
                var tag = NextTag();
                await WriteAsync($"{tag} AUTHENTICATE PLAIN\r\n");
                var cont = await ReadLineAsync();
                if (!cont.StartsWith('+'))
                    return Classify(ParseTagged(cont, tag).Status);
                var sasl = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + login + "\0" + password));
                await WriteAsync(sasl + "\r\n");
                status = (await ReadTaggedAsync(tag, null)).Status;
            }
            else if (Capabilities.Contains("LOGINDISABLED"))
            {
                throw new ImapProtocolException("The server disables LOGIN on this connection (use TLS or STARTTLS)");
            }
            else
            {
                var tag = NextTag();
                await WriteAsync($"{tag} LOGIN ");
                await WriteAStringAsync(login);
                await WriteAsync(" ");
                await WriteAStringAsync(password);
                await WriteAsync("\r\n");
                status = (await ReadTaggedAsync(tag, null)).Status;
            }
            return Classify(status);
        }

        private static ImapResult Classify(string status) => status switch
        {
            "OK" => ImapResult.Success,
            "NO" => ImapResult.InvalidCredentials,
            _ => throw new ImapProtocolException("Authentication command failed with " + status),
        };

        public async Task LogoutAsync()
        {
            try { await CommandAsync("LOGOUT"); }
            catch (Exception) { /* best effort */ }
        }

        private string NextTag() => "N" + (++_tag).ToString("D3");

        private async Task<(string Status, string Text)> CommandAsync(string command, Action<string>? untagged = null)
        {
            var tag = NextTag();
            await WriteAsync($"{tag} {command}\r\n");
            return await ReadTaggedAsync(tag, untagged);
        }

        private async Task<(string Status, string Text)> ReadTaggedAsync(string tag, Action<string>? untagged)
        {
            while (true)
            {
                var line = await ReadLineAsync();
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) return ParseTagged(line, tag);
                if (line.StartsWith('*')) untagged?.Invoke(line);
            }
        }

        private static (string Status, string Text) ParseTagged(string line, string tag)
        {
            if (!line.StartsWith(tag + " ", StringComparison.Ordinal)) throw new ImapProtocolException("Unexpected response: " + line);
            var rest = line[(tag.Length + 1)..];
            var space = rest.IndexOf(' ');
            return space < 0 ? (rest.ToUpperInvariant(), "") : (rest[..space].ToUpperInvariant(), rest[(space + 1)..]);
        }

        /// <summary>Writes a quoted string, or a synchronising literal for 8-bit or special content.</summary>
        private async Task WriteAStringAsync(string value)
        {
            var needsLiteral = value.Any(c => c > 126 || c < 32);
            if (!needsLiteral)
            {
                await WriteAsync("\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            await WriteAsync("{" + bytes.Length + "}\r\n");
            var cont = await ReadLineAsync();
            if (!cont.StartsWith('+')) throw new ImapProtocolException("Server rejected literal: " + cont);
            await _stream.WriteAsync(bytes, Ct);
        }

        private async Task WriteAsync(string text)
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
            await _stream.FlushAsync(Ct);
        }

        private async Task<string> ReadLineAsync()
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_bufferPos >= _bufferLen)
                {
                    _bufferLen = await _stream.ReadAsync(_buffer, Ct);
                    _bufferPos = 0;
                    if (_bufferLen == 0) throw new IOException("Connection closed by server");
                }
                var b = _buffer[_bufferPos++];
                if (b == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                    return Encoding.UTF8.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > 65536) throw new ImapProtocolException("Response line too long");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _tcp.Dispose();
            _cts.Dispose();
        }
    }
}

/// <summary>Resolves login names and verifies account passwords against the configured source.</summary>
public sealed class CredentialService(PasswordService passwords, ImapAuthenticator imap, SettingsService settings, ILogger<CredentialService> log)
{
    /// <summary>Finds an account by username or, failing that, by its (unique) email address.</summary>
    public async Task<User?> FindByLoginAsync(AppDbContext db, string login, CancellationToken ct = default)
    {
        login = login.Trim();
        if (login.Length == 0) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == login, ct);
        if (user != null || !login.Contains('@')) return user;
        var byEmail = await db.Users.Where(u => u.Email == login).Take(2).ToListAsync(ct);
        return byEmail.Count == 1 ? byEmail[0] : null;
    }

    public ImapEndpoint? EndpointFor(User user)
    {
        var s = settings.Current;
        var host = string.IsNullOrWhiteSpace(user.ImapHost) ? s.ImapHost : user.ImapHost;
        if (string.IsNullOrWhiteSpace(host)) return null;
        var custom = !string.IsNullOrWhiteSpace(user.ImapHost);
        var security = user.ImapSecurity ?? (custom ? MailSecurity.Tls : s.ImapSecurity);
        var port = user.ImapPort ?? (custom ? DefaultImapPort(security) : s.ImapPort);
        return new ImapEndpoint(host.Trim(), port, security, s.ImapAllowInvalidCertificates);
    }

    public static int DefaultImapPort(MailSecurity security) => security == MailSecurity.Tls ? 993 : 143;

    public static string ImapLoginFor(User user) =>
        !string.IsNullOrWhiteSpace(user.ImapLogin) ? user.ImapLogin.Trim() : user.Email ?? user.Username;

    /// <summary>Verifies the account password (local hash or IMAP passthrough). May upgrade the stored hash.</summary>
    public async Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct = default)
    {
        if (user.AuthSource == AuthSource.Imap)
        {
            var endpoint = EndpointFor(user);
            if (endpoint == null)
            {
                log.LogWarning("User {User} uses IMAP authentication but no IMAP server is configured", user.Username);
                return false;
            }
            return await imap.AuthenticateAsync(endpoint, ImapLoginFor(user), password, ct) == ImapResult.Success;
        }
        return passwords.Verify(user, password, out _);
    }
}
