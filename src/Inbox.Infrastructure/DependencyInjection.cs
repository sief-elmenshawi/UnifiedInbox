using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Inbound;
using Inbox.Infrastructure.Agents;
using Inbox.Infrastructure.Channels;
using Inbox.Infrastructure.Conversations;
using Inbox.Infrastructure.Identity;
using Inbox.Infrastructure.Inbound;
using Inbox.Infrastructure.Persistence;
using Inbox.Infrastructure.Seeding;
using Inbox.Infrastructure.Tenancy;
using Inbox.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inbox.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // This is the first thing the app needs, and the most common misconfiguration, so the
            // message names the environment and the exact commands instead of just the key.
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured (environment: '{environment.EnvironmentName}', " +
                $"content root: '{environment.ContentRootPath}'). " +
                "Fix it with one of: " +
                "dotnet user-secrets --project src/Inbox.Api set \"ConnectionStrings:Default\" \"<value>\", " +
                "the environment variable ConnectionStrings__Default, " +
                "or a local appsettings.Development.json copied from appsettings.Development.json.example. " +
                "Note that appsettings.Development.json and user-secrets are only loaded when " +
                "ASPNETCORE_ENVIRONMENT=Development - 'dotnet run' and F5 set that, running the built " +
                "Inbox.Api.exe directly does not.");
        }

        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(
            connectionString,
            sqlServer => sqlServer.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name)));

        // Identity without cookies: authentication is JWT-only, so no cookie scheme is registered
        // and UserManager is used purely for password hashing, lockout and security stamps.
        services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredUniqueChars = 4;

            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedAccount = false;
            options.SignIn.RequireConfirmedEmail = false;
        });

        // AddIdentity registers the Identity cookie handlers, but the API is JWT-only: the caller
        // sets DefaultAuthenticateScheme/DefaultChallengeScheme to "Bearer" explicitly, so no
        // cookie scheme is ever selected. AddIdentity is used because it is the only overload that
        // binds RoleManager/UserManager to IdentityRole<Guid>, matching the DbContext's key type.
        services.AddIdentity<Agent, IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IAccessTokenService, AccessTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAgentsService, AgentsService>();
        services.AddScoped<IConversationsService, ConversationsService>();
        services.AddScoped<IChannelsService, ChannelsService>();
        services.AddScoped<IInboundProcessor, InboundProcessor>();
        services.AddScoped<IWorkspaceContext, SingleWorkspaceContext>();
        services.AddScoped<IDatabaseSeeder, DatabaseSeeder>();

        // One adapter per channel type; the factory resolves by ChannelType, so adding a channel is
        // a new adapter class and nothing else.
        services.AddSingleton<IChannelAdapter, TestChannelAdapter>();
        services.AddSingleton<IChannelAdapterFactory, ChannelAdapterFactory>();

        // Channel credentials are encrypted at rest. The key ring lives outside the database
        // (user profile or the host's key store), so a database dump alone yields nothing usable.
        services.AddDataProtection();
        services.AddSingleton<IDataProtector>(sp => sp
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ChannelsService.ProtectorPurpose));

        // The queue is a singleton so the webhook endpoint and the worker share it; the processor
        // behind it is scoped, because it needs a DbContext per message.
        services.AddSingleton<IInboundMessageQueue, InboundMessageQueue>();
        services.AddHostedService<InboundProcessorWorker>();

        return services;
    }
}
