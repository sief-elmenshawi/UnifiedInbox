using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Inbox.Api.Extensions;

public static class DatabaseExtensions
{
    /// <summary>
    /// Applies pending migrations and seeds the first admin, but only in Development.
    /// Production must apply migrations as a separate deployment step, e.g.
    /// <c>dotnet ef migrations script --idempotent</c> or <c>dotnet ef migrations bundle</c>.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation(
                "Skipping automatic database initialization outside Development. Apply migrations and seeding as a deployment step.");
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialization");

        logger.LogInformation("Applying database migrations.");
        await db.Database.MigrateAsync(cancellationToken);

        var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder>();
        await seeder.SeedAsync(cancellationToken);
    }
}