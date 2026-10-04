using FluentValidation;
using Inbox.Api.Controllers;
using Inbox.Api.Extensions;
using Inbox.Api.Middleware;
using Inbox.Application;
using Inbox.Application.Common;
using Inbox.Application.Options;
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

// AddApplication() scans the Application assembly; this covers the validators that live here
// (the dev simulator request).
builder.Services.AddValidatorsFromAssemblyContaining<DevController>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

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

await app.InitializeDatabaseAsync();

app.Run();

/// <summary>Exposed so the integration test project can host the API with WebApplicationFactory.</summary>
public partial class Program;