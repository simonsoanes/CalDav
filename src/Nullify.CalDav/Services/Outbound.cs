// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Nullify.CalDav.Data;

namespace Nullify.CalDav.Services;

/// <summary>
/// HTTP clients for server-initiated requests (calendar subscriptions, Web Push). Unless allowed, connections to
/// loopback, private, link-local and other non-public addresses are refused at connect time (also after redirects
/// and DNS changes), preventing server-side request forgery against internal services.
/// </summary>
public sealed class OutboundHttp : IDisposable
{
    private readonly HttpClient _public;
    private readonly HttpClient _any;

    public OutboundHttp()
    {
        _public = Create(allowPrivate: false);
        _any = Create(allowPrivate: true);
    }

    public HttpClient Client(bool allowPrivate) => allowPrivate ? _any : _public;

    private static HttpClient Create(bool allowPrivate)
    {
        var handler = CreateHandler(allowPrivate);
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 5;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 25 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nullify.CalDav/1.0");
        return client;
    }

    /// <summary>Creates a handler that enforces the address policy at connect time (used for per-job clients with credentials).</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => allowPrivate || IsPublic(a)).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"Connections to {context.DnsEndPoint.Host} are not allowed (non-public address).");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            return true;
        }
        var b = address.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                 (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                 (b[0] == 169 && b[1] == 254) ||
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                 (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                 (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
    }

    public void Dispose()
    {
        _public.Dispose();
        _any.Dispose();
    }
}

public sealed record OutgoingMail(string From, string? ReplyTo, string To, string Subject, string Body);

/// <summary>Minimal SMTP submission client (RFC 5321, STARTTLS RFC 3207, AUTH PLAIN/LOGIN RFC 4954).</summary>
public sealed class SmtpSender(SettingsService settings, ILogger<SmtpSender> log)
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        var s = settings.Current;
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) throw new InvalidOperationException("No SMTP server configured");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(s.SmtpHost, s.SmtpPort, cts.Token);
        Stream stream = tcp.GetStream();
        if (s.SmtpSecurity == MailSecurity.Tls) stream = await UpgradeAsync(stream, s.SmtpHost, cts.Token);
        var session = new Session(stream, cts.Token);

        await session.ExpectAsync(220);
        var ehlo = await session.CommandAsync("EHLO " + Environment.MachineName.ToLowerInvariant(), 250);
        if (s.SmtpSecurity == MailSecurity.StartTls)
        {
            await session.CommandAsync("STARTTLS", 220);
            session.Stream = await UpgradeAsync(session.Stream, s.SmtpHost, cts.Token);
            ehlo = await session.CommandAsync("EHLO " + Environment.MachineName.ToLowerInvariant(), 250);
        }

        if (!string.IsNullOrEmpty(s.SmtpUsername))
        {
            var auth = ehlo.FirstOrDefault(l => l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (auth.Contains("PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + s.SmtpUsername + "\0" + s.SmtpPassword));
                await session.CommandAsync("AUTH PLAIN " + token, 235);
            }
            else
            {
                await session.CommandAsync("AUTH LOGIN", 334);
                await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(s.SmtpUsername)), 334);
                await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(s.SmtpPassword ?? "")), 235);
            }
        }

        await session.CommandAsync($"MAIL FROM:<{mail.From}>", 250);
        await session.CommandAsync($"RCPT TO:<{mail.To}>", 250, 251);
        await session.CommandAsync("DATA", 354);
        var sb = new StringBuilder();
        foreach (var line in mail.Body.Replace("\r\n", "\n").Split('\n'))
            sb.Append(line.StartsWith('.') ? "." + line : line).Append("\r\n");
        sb.Append(".\r\n");
        await session.WriteAsync(sb.ToString());
        await session.ExpectAsync(250);
        try { await session.CommandAsync("QUIT", 221); } catch (Exception) { }
        log.LogInformation("Sent scheduling message to {To}", mail.To);
    }

    private static async Task<Stream> UpgradeAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
        return ssl;
    }

    private sealed class Session(Stream stream, CancellationToken ct)
    {
        public Stream Stream { get; set; } = stream;
        private readonly byte[] _buffer = new byte[4096];
        private int _pos, _len;

        public async Task WriteAsync(string text)
        {
            await Stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await Stream.FlushAsync(ct);
        }

        public async Task<List<string>> CommandAsync(string command, params int[] expected)
        {
            await WriteAsync(command + "\r\n");
            return await ExpectAsync(expected);
        }

        public async Task<List<string>> ExpectAsync(params int[] expected)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync();
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out var code)) throw new IOException("Bad SMTP reply: " + line);
                lines.Add(line.Length > 4 ? line[4..] : "");
                if (line.Length > 3 && line[3] == '-') continue;
                if (!expected.Contains(code)) throw new IOException($"SMTP error: {line}");
                return lines;
            }
        }

        private async Task<string> ReadLineAsync()
        {
            var bytes = new List<byte>();
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = await Stream.ReadAsync(_buffer, ct);
                    _pos = 0;
                    if (_len == 0) throw new IOException("SMTP connection closed");
                }
                var b = _buffer[_pos++];
                if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(b);
            }
        }
    }
}

/// <summary>Background queue for outgoing scheduling (iMIP) messages, with simple retry.</summary>
public sealed class MailQueue(SmtpSender sender, ILogger<MailQueue> log) : BackgroundService
{
    private readonly Channel<(OutgoingMail Mail, int Attempt)> _channel = Channel.CreateUnbounded<(OutgoingMail, int)>();

    public void Enqueue(OutgoingMail mail) => _channel.Writer.TryWrite((mail, 0));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (mail, attempt) in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await sender.SendAsync(mail, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("Sending to {To} failed (attempt {Attempt}): {Message}", mail.To, attempt + 1, ex.Message);
                if (attempt < 4)
                {
                    _ = Task.Delay(TimeSpan.FromMinutes(Math.Pow(2, attempt)), stoppingToken)
                        .ContinueWith(_ => _channel.Writer.TryWrite((mail, attempt + 1)), TaskScheduler.Default);
                }
            }
        }
    }
}
