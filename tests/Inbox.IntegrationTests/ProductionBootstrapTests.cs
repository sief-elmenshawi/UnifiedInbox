using FluentAssertions;
using Inbox.Api.Extensions;
using Inbox.Application.Auth;
using Inbox.Application.Options;
using Inbox.Domain.Entities;
using Inbox.Domain.Enums;
using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Inbox.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inbox.IntegrationTests;

/// <summary>
/// Getting the first usable account onto a fresh production instance.
///
/// <para>
/// Development seeds itself on every boot. Production deliberately does not - the API there never
/// migrates or seeds, because an instance that does either on startup is an instance that races its own
/// replicas and its own traffic - which leaves a gap the deployment has to fill. These tests cover the
/// fill: the command exists, it is idempotent enough to be re-run, and the password it installs is
/// treated as a temporary one, because a password that arrived in a deployment configuration has been
/// seen by whatever put it there.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ProductionBootstrapTests(ApiFactory factory) : IAsyncLifetime
{
    /// <summary>
    /// The suite shares one database, and "is this the last active admin" is a question about the whole
    /// database rather than about the account being acted on. An admin left active here would make every
    /// last-admin guard that runs afterwards quietly assert nothing, which is worse than having no guard
    /// test at all. So the accounts this class creates are deactivated again in teardown - including when
    /// the test that created one failed part way through and never got to clean up after itself.
    /// </summary>
    private const string SeededEmailPrefix = "bootstrap.";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Agents
            .Where(x => x.Email != null && x.Email.StartsWith(SeededEmailPrefix) && x.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsActive, false));
    }

    [Fact]
    public void The_seed_command_is_a_command_and_not_a_server_start()
    {
        DatabaseExtensions.Commands.IsCommand(["seed-admin"]).Should().BeTrue();
        DatabaseExtensions.Commands.Requested(["--urls", "http://localhost:0", "seed-admin"])
            .Should().Be(DatabaseExtensions.Commands.SeedAdmin);

        // Case does not matter, because it is typed by a person at a terminal.
        DatabaseExtensions.Commands.IsCommand(["SEED-ADMIN"]).Should().BeTrue();

        // The part that matters most: ordinary arguments are not commands, or a misconfigured --urls
        // would silently run migrations instead of starting the server.
        DatabaseExtensions.Commands.IsCommand([]).Should().BeFalse();
        DatabaseExtensions.Commands.IsCommand(["--urls", "http://localhost:5000"]).Should().BeFalse();
        DatabaseExtensions.Commands.Requested(["--urls", "http://localhost:5000"]).Should().BeNull();
    }

    [Fact]
    public async Task An_admin_seeded_outside_development_owes_a_password_change()
    {
        var seed = ProductionSeed();

        var seeded = await SeedAsync(seed);

        seeded.Role.Should().Be(AgentRole.Admin);
        seeded.IsActive.Should().BeTrue();
        seeded.MustChangePassword.Should().BeTrue(
            "the seeded password came from a deployment configuration, so the account that uses it has to replace it");

        // And the flag has to reach the client, or the UI cannot ask for the change. The sign-in itself
        // is allowed: refusing it would leave nobody able to perform the change.
        var session = await ApiClient.LoginAsync(factory, seed.AdminEmail, seed.AdminPassword);

        session.Tokens.Agent.MustChangePassword.Should().BeTrue();
        session.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Re_running_the_seed_does_not_touch_a_password_that_was_changed()
    {
        var seed = ProductionSeed();

        await SeedAsync(seed);

        // The first sign-in changes it, which is the whole point of the forced change.
        var session = await ApiClient.LoginAsync(factory, seed.AdminEmail, seed.AdminPassword);
        var changed = ApiClient.NewPassword();

        (await session.Client.PostAsync(
            "/api/auth/change-password",
            new ChangePasswordRequest(seed.AdminPassword, changed)))
            .EnsureSuccessStatusCode();

        // A deployment re-runs its steps. If the seed reset the password, the next deploy would lock the
        // operator out of the instance they are trying to fix.
        var again = await SeedAsync(seed);

        again.MustChangePassword.Should().BeFalse("the change was made and must not be undone by a re-run");
        again.Id.Should().Be((await SeedAsync(seed)).Id);

        // Proof that the new password is the one in force, rather than trusting the flag above.
        var afterReseed = await ApiClient.LoginAsync(factory, seed.AdminEmail, changed);

        afterReseed.Tokens.Agent.MustChangePassword.Should().BeFalse();

        // Leave the shared test database in a state where the temporary password is the working one, in
        // case anything else in this test class ever signs in as this account.
        await using var cleanup = factory.Services.CreateAsyncScope();
        var users = cleanup.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Agent>>();

        var account = await users.FindByEmailAsync(seed.AdminEmail);

        if (account is not null)
        {
            await users.RemovePasswordAsync(account);
            await users.AddPasswordAsync(account, seed.AdminPassword);
        }
    }

    private static SeedOptions ProductionSeed() => new()
    {
        // A workspace and an account of its own per test: the shared database already holds the
        // Development seed's workspace, and these assertions are about what the seeder creates, not
        // about what is already there.
        WorkspaceName = $"Bootstrap {Guid.NewGuid():N}",
        AdminEmail = ApiClient.NewEmail("bootstrap"),
        AdminDisplayName = "Bootstrap Admin",
        AdminPassword = ApiClient.NewPassword()
    };

    /// <summary>
    /// Runs the real seeder with the real database, pretending the environment is Production. The
    /// environment is a constructor dependency rather than an option, which is why the seeder is
    /// composed here instead of resolved: the container's instance would answer Development and the
    /// forced password change would never be exercised.
    /// </summary>
    private async Task<Agent> SeedAsync(SeedOptions options)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        var seeder = ActivatorUtilities.CreateInstance<DatabaseSeeder>(
            scope.ServiceProvider,
            Options.Create(options),
            new ProductionEnvironment());

        await seeder.SeedAsync();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Agents.AsNoTracking().SingleAsync(x => x.Email == options.AdminEmail);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Inbox.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
