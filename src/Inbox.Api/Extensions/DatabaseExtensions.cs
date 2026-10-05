using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Api.Extensions;

public static class DatabaseExtensions
{
    /// <summary>
    /// The one-shot administrative commands the API image accepts instead of serving traffic.
    ///
    /// <para>
    /// A command is a deployment step, not a mode the server runs in: it applies migrations, does its
    /// one job and exits. That is what keeps the risky parts of a release - schema changes, creating the
    /// first account - out of the hands of whatever traffic the instance happens to receive, and out of
    /// the hands of a second replica booting at the same moment.
    /// </para>
    /// </summary>
    public static class Commands
    {
        /// <summary>
        /// Applies pending migrations and creates the first admin from the <c>Seed</c> configuration.
        /// Idempotent, so it is safe to re-run: an existing admin is left alone.
        /// </summary>
        public const string SeedAdmin = "seed-admin";

        private static readonly string[] All = [SeedAdmin];

        /// <summary>True when <paramref name="args"/> asks for a command rather than for the server.</summary>
        public static bool IsCommand(string[] args) =>
            args.Any(argument => All.Contains(argument, StringComparer.OrdinalIgnoreCase));

        /// <summary>The command named in <paramref name="args"/>, or null when the server should start.</summary>
        public static string? Requested(string[] args) =>
            args.FirstOrDefault(argument => All.Contains(argument, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies pending migrations and seeds the first admin, but only in Development.
    /// Production must apply migrations as a separate deployment step, e.g.
    /// <c>dotnet ef migrations script --idempotent</c> or <c>dotnet ef migrations bundle</c>, and run
    /// <see cref="Commands.SeedAdmin"/> for the first account.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation(
                "Skipping automatic database initialization outside Development. Apply migrations as a " +
                "deployment step, then run the {Command} command for the first admin.",
                Commands.SeedAdmin);
            return;
        }

        await ApplyMigrationsAndSeedAsync(app, cancellationToken);
    }

    /// <summary>
    /// The production counterpart of the Development startup path: migrate, seed, stop. Applying
    /// migrations belongs here rather than in the guarded startup path because this runs once, from a
    /// deployment, and not from N replicas booting at the same time.
    /// </summary>
    public static async Task ApplyMigrationsAndSeedAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialization");

        logger.LogInformation("Applying database migrations.");
        await db.Database.MigrateAsync(cancellationToken);

        var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>();
        await seeder.SeedAsync(cancellationToken);

        logger.LogInformation(
            "Database ready. A seeded admin must change the password at first sign-in unless this ran in Development.");
    }
}
