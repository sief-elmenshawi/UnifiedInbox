using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    private const string SigningKey = "integration_test_signing_key_0123456789";

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
        "RateLimiting__WebhookPermitLimit",
        "Serilog__MinimumLevel__Default",
        "Serilog__WriteTo__1__Name",
        "Serilog__WriteTo__1__Args__path"
    ];

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithDatabase(DatabaseName)
        .Build();

    private bool _containerStopped;

    private readonly List<IHostedService> _startedWorkers = [];

    /// <summary>Where the API writes its log during the run; see <see cref="InitializeAsync"/>.</summary>
    public string TestLogPath { get; private set; } = string.Empty;

    // Explicit interface implementations: xUnit's IAsyncLifetime uses Task while
    // WebApplicationFactory uses ValueTask, so the public members keep the base signatures.
    async Task IAsyncLifetime.InitializeAsync() => await InitializeAsync();

    Task IAsyncLifetime.DisposeAsync() => TearDownAsync();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Set("ConnectionStrings__Default", _container.GetConnectionString());
        Set("Jwt__Issuer", "inbox-integration-tests");
        Set("Jwt__Audience", "inbox-integration-tests-api");
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
        Set("RateLimiting__WebhookPermitLimit", "10000");
        Set("Serilog__MinimumLevel__Default", "Warning");

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