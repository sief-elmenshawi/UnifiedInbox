using Serilog;
using Serilog.Events;

namespace Inbox.Api.Extensions;

public static class SerilogExtensions
{
    /// <summary>
    /// Configures Serilog from the "Serilog" configuration section, with sensible defaults so the
    /// API still logs structured output when the section is absent.
    /// </summary>
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Inbox.Api")
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning));

        return builder;
    }
}