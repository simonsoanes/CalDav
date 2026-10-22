using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Tests;

/// <summary>One application instance (and fake IMAP server) shared by all integration tests.</summary>
[TestClass]
public static class TestHost
{
    private static readonly SemaphoreSlim Init = new(1, 1);
    private static WebApplicationFactory<Program>? _factory;
    private static string _dataDir = null!;

    public static FakeImapServer Imap { get; } = new();

    public static async Task<WebApplicationFactory<Program>> GetAsync()
    {
        if (_factory != null) return _factory;
        await Init.WaitAsync();
        try
        {
            if (_factory != null) return _factory;
            _dataDir = Path.Combine(Path.GetTempPath(), "nullify-caldav-tests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("Storage__DataDirectory", _dataDir);
            var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync("alice", "Alice", "alice@example.com", "alice-password", isAdmin: true);
            await users.CreateAsync("bob", "Bob", "bob@example.com", "bob-password", isAdmin: false);
            var carol = await users.CreateAsync("carol", "Carol", "carol@example.com", null, isAdmin: false);
            carol.AuthSource = AuthSource.Imap;
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
            await users.CreateAsync("syncer", "Sync User", "syncer@example.com", "syncer-password", isAdmin: false);

            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SaveAsync(settings.Current with
            {
                PublicUrl = "https://dav.example.com",
                MailDomains = "example.com",
                MailProviderName = "Example Mail",
                ImapHost = "127.0.0.1",
                ImapPort = Imap.Port,
                ImapSecurity = MailSecurity.None,
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                SmtpSecurity = MailSecurity.StartTls,
                SubscriptionsAllowPrivateNetworks = true,
                PushContact = "admin@example.com",
            });
            _factory = factory;
            return factory;
        }
        finally
        {
            Init.Release();
        }
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        _factory?.Dispose();
        Imap.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (_dataDir != null) Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    public static async Task<HttpClient> ClientAsync(string? user = null, string? password = null)
    {
        var factory = await GetAsync();
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user != null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return c;
    }
}

/// <summary>Tiny IMAP server accepting carol@example.com / imap-secret.</summary>
public sealed class FakeImapServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public bool OfferAuthPlain { get; set; } = true;
    public int Logins;

    public FakeImapServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Handle(client);
        }
    }

    private static bool Check(string user, string pass) => user == "carol@example.com" && pass == "imap-secret";

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("* OK Fake IMAP ready");
        while (await reader.ReadLineAsync() is { } line)
        {
            var sp = line.IndexOf(' ');
            var tag = line[..sp];
            var cmd = line[(sp + 1)..];
            if (cmd.Equals("CAPABILITY", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* CAPABILITY IMAP4rev1" + (OfferAuthPlain ? " AUTH=PLAIN" : ""));
                await writer.WriteLineAsync($"{tag} OK done");
            }
            else if (cmd.Equals("AUTHENTICATE PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("+ ");
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!)).Split('\0');
                Interlocked.Increment(ref Logins);
                await writer.WriteLineAsync(Check(parts[1], parts[2]) ? $"{tag} OK welcome" : $"{tag} NO [AUTHENTICATIONFAILED] nope");
            }
            else if (cmd.StartsWith("LOGIN ", StringComparison.OrdinalIgnoreCase))
            {
                var args = cmd[6..].Split("\" \"");
                var user = args[0].Trim('"');
                var pass = args[1].Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");
                Interlocked.Increment(ref Logins);
                await writer.WriteLineAsync(Check(user, pass) ? $"{tag} OK welcome" : $"{tag} NO nope");
            }
            else if (cmd.Equals("LOGOUT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("* BYE");
                await writer.WriteLineAsync($"{tag} OK bye");
                return;
            }
            else await writer.WriteLineAsync($"{tag} BAD unknown");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
