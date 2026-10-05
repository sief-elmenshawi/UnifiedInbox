using FluentValidation;
using Inbox.Api.Controllers;
using Inbox.Api.Extensions;
using Inbox.Api.Hubs;
using Inbox.Api.Middleware;
using Inbox.Api.Realtime;
using Inbox.Application;
using Inbox.Application.Common;
using Inbox.Application.Options;
using Inbox.Application.Realtime;
using Inbox.Infrastructure;
using Inbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

builder.Services.AddOptions<SeedOptions>()
    .Bind(builder.Configuration.GetSection(SeedOptions.SectionName));

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication();
builder.Services.AddApiAuthentication(builder.Configuration);
builder.Services.AddApiCors(builder.Configuration);
builder.Services.AddApiRateLimiting(builder.Configuration);

// The dev-only controllers live in this assembly, so the part manager is what lets them be
// stripped entirely outside Development - see DevOnlyControllerFeatureProvider.
builder.Services.AddControllers()
    .AddApplicationPart(typeof(DevController).Assembly)
    .ConfigureApplicationPartManager(manager =>
        manager.FeatureProviders.Add(new DevOnlyControllerFeatureProvider(builder.Environment)))
    .ConfigureApiBehaviorOptions(options =>
    {
        // One error shape for the whole API: MVC's automatic 400 is replaced by the same
        // problem-details document (and 422 status) that the Result mapping produces.
        options.InvalidModelStateResponseFactory = context =>
        {
            var problem = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Validation failed",
                Type = "https://httpstatuses.io/422",
                Instance = context.HttpContext.Request.Path
            };

            problem.Extensions["errorCode"] = ValidationResultExtensions.DefaultCode;
            problem.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);

            return new ObjectResult(problem) { StatusCode = StatusCodes.Status422UnprocessableEntity };
        };
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGenWithBearerAuth();
builder.Services.AddSignalR();

// AddApplication() scans the Application assembly; this covers the validators that live here
// (the dev simulator request).
builder.Services.AddValidatorsFromAssemblyContaining<DevController>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// The live-update port. Application and Infrastructure publish through it; the SignalR adapter is
// registered here because the hub is a transport concern of this host. The registry has to be a
// singleton - it is the shared map of live connections and which threads they have open, and the
// hub needs one instance while the scoped notifier needs to reach the same one.
builder.Services.AddSingleton<InboxConnectionRegistry>();
builder.Services.AddScoped<IInboxNotifier, SignalRInboxNotifier>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

// A command instead of a server: apply migrations, create the first admin, exit. Handled before the
// pipeline is built so `seed-admin` never opens a port, and before the first request could race the
// schema it is about to change. Outside Development this is the only supported way to create the first
// account, because startup deliberately does not migrate or seed there.
if (DatabaseExtensions.Commands.IsCommand(args))
{
    var command = DatabaseExtensions.Commands.Requested(args);

    var commandApp = builder.Build();

    await commandApp.ApplyMigrationsAndSeedAsync();

    return;
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

// Must sit between the logging/correlation middleware and the endpoint routing so the named
// policies ([EnableRateLimiting]) apply. Without this call the AddRateLimiter registrations in
// AddApiRateLimiting are never activated and every endpoint is unmetered.
app.UseRateLimiter();

app.UseApiCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.MapControllers();

// Live inbox. The token arrives as ?access_token= because a WebSocket handshake cannot carry an
// Authorization header; AuthenticationExtensions maps it back to the bearer scheme for this route.
app.MapHub<InboxHub>(InboxHub.Route);

await app.InitializeDatabaseAsync();

app.Run();

/// <summary>Exposed so the integration test project can host the API with WebApplicationFactory.</summary>
public partial class Program;