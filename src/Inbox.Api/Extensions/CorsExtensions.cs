using Inbox.Api.Options;
using Microsoft.Extensions.Options;

namespace Inbox.Api.Extensions;

public static class CorsExtensions
{
    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration)
    {
        var cors = configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();

        if (cors.AllowedOrigins.Length == 0)
        {
            throw new InvalidOperationException(
                $"'{CorsOptions.SectionName}:AllowedOrigins' must list the Angular origin, for example http://localhost:4200.");
        }

        if (cors.AllowedOrigins.Any(origin => origin == "*"))
        {
            throw new InvalidOperationException(
                $"'{CorsOptions.SectionName}:AllowedOrigins' must not contain '*' because the API authenticates with a bearer token and allows credentials.");
        }

        services.AddCors(options => options.AddPolicy(CorsOptions.PolicyName, policy => policy
            .WithOrigins(cors.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        return services;
    }

    public static IApplicationBuilder UseApiCors(this WebApplication app) =>
        app.UseCors(CorsOptions.PolicyName);
}