using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inbox.IntegrationTests.Infrastructure;

/// <summary>
/// A second host of the same API, started as Production, against the database container already
/// provided by <see cref="ApiFactory"/> (the connection string is in the process environment).
/// Used to prove that the dev-only endpoints do not exist outside Development.
/// </summary>
public sealed class ProductionApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
    }
}