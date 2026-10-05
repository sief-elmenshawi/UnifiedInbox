using System.Text;
using Inbox.Infrastructure.Channels.WhatsApp;
using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API against a throwaway SQL Server container, seeded with one admin account.
/// Settings are pushed in as environment variables because the container connection string is only
/// known after the container starts, and environment variables are the one configuration source the
/// application reads regardless of how the test host is composed.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@inbox.test";
    public const string AdminPassword = "Test_Admin_Password1!";
    public const string ClientOrigin = "http://localhost:4200";

    private const string DatabaseName = "InboxTests";

    /// <summary>
    /// The key this factory hands to the host, so a test can sign a token of its own - an expired one,
    /// for instance. A test that could only use tokens the host minted could never test expiry.
    /// </summary>
    internal const string SigningKey = "integration_test_signing_key_0123456789";

    internal const string Issuer = "inbox-integration-tests";
    internal const string Audience = "inbox-integration-tests-api";

    private static readonly string[] OverriddenVariables =
    [
        "ConnectionStrings__Default",
        "Jwt__Issuer",
        "Jwt__Audience",
        "Jwt__SigningKey",
        "Jwt__AccessTokenMinutes",
        "Jwt__RefreshTokenDays",
        "Seed__WorkspaceName",
        "Seed__AdminEmail",
        "Seed__AdminDisplayName",
        "Seed__AdminPassword",
        "Cors__AllowedOrigins__0",
        "RateLimiting__LoginPermitLimit",
        "RateLimiting__RefreshPermitLimit",
        "RateLimiting__WebhookAcceptedPermitLimit",
        "RateLimiting__WebhookFailurePermitLimit",
        "Meta__GraphApiVersion",
        "Meta__GraphBaseUrl",
        "Serilog__MinimumLevel__Default",
        "Serilog__WriteTo__1__Name",
        "Serilog__WriteTo__1__Args__path"
    ];

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithDatabase(DatabaseName)
        .Build();

    private bool _containerStopped;

    private readonly List<IHostedService> _startedWorkers = [];

    /// <summary>
    /// The path handed to the file sink. Note that this is not necessarily the file on disk: Serilog's
    /// file sink appends the date to a path with no rolling placeholder, so use
    /// <see cref="ReadTestLogAsync"/> rather than opening this path.
    /// </summary>
    public string TestLogPath { get; private set; } = string.Empty;

    /// <summary>Whatever the API has written to its log file so far.</summary>
    public async Task<string> ReadTestLogAsync()
    {
        var directory = Path.GetDirectoryName(TestLogPath)!;
        var prefix = Path.GetFileNameWithoutExtension(TestLogPath);
        var written = Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{prefix}*")
            : [];

        if (written.Length == 0)
        {
            return string.Empty;
        }

var builder = new StringBuilder();

        // Opened with FileShare so a test can read while the API still holds the sink open, and with
        // Delete as well: the sink appends the date to the name, so a file can be rotated out from under
        // a reader. The read is retried, because a file caught mid-write is not a broken test - and a
        // log read that throws reports a failing suite instead of an unwritten line.
        foreach (var file in written)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);

                    builder.AppendLine(await reader.ReadToEndAsync());
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(50);
                }
                catch (FileNotFoundException)
                {
                    // Rotated away between the glob and the open.
                    break;
                }
            }
        }

        return builder.ToString();
    }

    // Explicit interface implementations: xUnit's IAsyncLifetime uses Task while
    // WebApplicationFactory uses ValueTask, so the public members keep the base signatures.
    async Task IAsyncLifetime.InitializeAsync() => await InitializeAsync();

    Task IAsyncLifetime.DisposeAsync() => TearDownAsync();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Set("ConnectionStrings__Default", _container.GetConnectionString());
        Set("Jwt__Issuer", Issuer);
        Set("Jwt__Audience", Audience);
        Set("Jwt__SigningKey", SigningKey);
        Set("Jwt__AccessTokenMinutes", "15");
        Set("Jwt__RefreshTokenDays", "14");
        Set("Seed__WorkspaceName", "Integration Tests");
        Set("Seed__AdminEmail", AdminEmail);
        Set("Seed__AdminDisplayName", "Test Admin");
        Set("Seed__AdminPassword", AdminPassword);
        Set("Cors__AllowedOrigins__0", ClientOrigin);

        // The whole suite logs in from 127.0.0.1, so the per-IP limiter would otherwise rate limit
        // the tests themselves. Brute-force protection is covered by the Identity lockout instead.
        Set("RateLimiting__LoginPermitLimit", "10000");
        Set("RateLimiting__RefreshPermitLimit", "10000");

        // Same reason for the webhook's per-address failure budget: every forged payload in the suite
        // comes from 127.0.0.1, so a low budget would be spent by the tests testing it. The host with the
        // limits actually turned down lives in ThrottledWebhookFactory.
        Set("RateLimiting__WebhookAcceptedPermitLimit", "10000");
        Set("RateLimiting__WebhookFailurePermitLimit", "10000");

        // The graph host is stubbed, but the version is not optional: the client refuses to build a
        // URL without it, and a send has to look configured for the send tests to mean anything.
        Set("Meta__GraphApiVersion", "v21.0");
        Set("Meta__GraphBaseUrl", "https://graph.facebook.com");
        // Information so that a *successful* request is logged, not only a failing one. The token tests
        // need a hub line they can rely on being there, and Serilog only writes 2xx requests at Information.
        Set("Serilog__MinimumLevel__Default", "Information");

        // ...but not EF and Kestrel chatter, which is most of what a request's worth of Information is.
        Set("Serilog__MinimumLevel__Override__Microsoft", "Warning");

        // "dotnet test" hides the console sink, and a 500 with no exception behind it is impossible
        // to debug, so the suite keeps its own log file next to the test binaries.
        TestLogPath = Path.Combine(AppContext.BaseDirectory, $"inbox-tests-{Guid.NewGuid():N}.log");
        Set("Serilog__WriteTo__1__Name", "File");
        Set("Serilog__WriteTo__1__Args__path", TestLogPath);

        // WebApplicationFactory stops Program before app.Run(), so Program's own
        // InitializeDatabaseAsync never executes under test: migrate and seed here instead.
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>().SeedAsync();

        // Same reason: the host is built but never started, so hosted services are not running. The
        // inbound queue worker is one of them, and the tests are supposed to prove that an injected
        // message is picked up asynchronously, so it has to be started by hand.
        foreach (var hosted in Services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
            _startedWorkers.Add(hosted);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Nothing in the suite may reach Meta. The stub answers with the same JSON shape the graph
        // host does, so the adapter sees a realistic response and no test depends on a real number.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<WhatsAppGraphClient>()
                .ConfigurePrimaryHttpMessageHandler(() => StubGraph.Handler));
    }

    private async Task TearDownAsync()
    {
        if (_containerStopped)
        {
            return;
        }

        _containerStopped = true;

        foreach (var hosted in _startedWorkers)
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        _startedWorkers.Clear();

        foreach (var name in OverriddenVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        await _container.DisposeAsync();
    }

    private static void Set(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value);
}