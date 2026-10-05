using Inbox.Application.Agents;
using Inbox.Application.Auth;
using Inbox.Application.Channels;
using Inbox.Application.Common;
using Inbox.Application.Conversations;
using Inbox.Application.Inbound;
using Inbox.Application.Options;
using Inbox.Infrastructure.Agents;
using Inbox.Infrastructure.Channels;
using Inbox.Infrastructure.Channels.WhatsApp;
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
        //
        // The test adapter is the exception: it fabricates inbound messages and reports sends as
        // delivered, so it is only ever registered where the simulator that drives it is available.
        // A Test channel row that reached a production database therefore has no adapter at all,
        // which fails loudly instead of quietly accepting fake conversations.
        if (environment.IsDevelopment())
        {
            services.AddSingleton<IChannelAdapter, TestChannelAdapter>();
        }

        services.AddSingleton<IChannelAdapter, WhatsAppChannelAdapter>();
        services.AddSingleton<IChannelAdapterFactory, ChannelAdapterFactory>();

        // Credentials are decrypted on demand, in one place, so the webhook endpoint and the adapters
        // never need to know about Data Protection.
        services.AddSingleton<IChannelSecrets, ChannelSecrets>();

        // Channel credentials are encrypted at rest. The key ring lives outside the database
        // (user profile or the host's key store), so a database dump alone yields nothing usable.
        services.AddDataProtection();
        services.AddSingleton<IDataProtector>(sp => sp
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ChannelsService.ProtectorPurpose));

        // The outbox is the hand-off: the endpoint appends a verified payload, the worker drains the
        // table. The signal is a singleton because both sides must share it, and it only ever says
        // "there is something due" - the payload is in the database.
        services.AddScoped<IInboundWebhookStore, WebhookEventStore>();
        services.AddSingleton<IInboundWakeupSignal, InboundWakeupSignal>();
        services.AddHostedService<InboundProcessorWorker>();

        // Retention, validated on start: a misconfigured value that silently became "purge nothing" or
        // "purge everything" would be discovered by the disk, months later. The cross-field rule is the
        // one that matters - failures must outlive successes, or an operator's queue would be aged out
        // before they got to it.
        services.AddOptions<InboundOptions>()
            .Bind(configuration.GetSection(InboundOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.FailedRetentionDays >= options.ProcessedRetentionDays,
                "Inbound:FailedRetentionDays must be at least Inbound:ProcessedRetentionDays, because a " +
                "delivery that failed is the one an operator still has to act on.")
            .ValidateOnStart();

        // The WhatsApp Cloud API. The Graph version is configuration (Meta versions it), and sending
        // without one fails with a message naming the setting instead of a confusing 404 - which
        // means the webhook side keeps working on an instance that has not set it yet.
        services.AddOptions<MetaOptions>()
            .Bind(configuration.GetSection(MetaOptions.SectionName));

        services.AddHttpClient<WhatsAppGraphClient>()
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20));

        services.AddSingleton<WhatsAppWebhookParser>();

        return services;
    }
}
