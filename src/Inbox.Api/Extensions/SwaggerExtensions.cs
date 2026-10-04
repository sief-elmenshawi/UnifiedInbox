using Microsoft.OpenApi.Models;

namespace Inbox.Api.Extensions;

public static class SwaggerExtensions
{
    public const string BearerScheme = "Bearer";

    public static IServiceCollection AddSwaggerGenWithBearerAuth(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Unified Inbox API",
                Version = "v1",
                Description = "Internal omnichannel customer support API."
            });

            var scheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Paste the access token returned by /api/auth/login.",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = BearerScheme
                }
            };

            options.AddSecurityDefinition(BearerScheme, scheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [scheme] = Array.Empty<string>()
            });
        });

        return services;
    }
}