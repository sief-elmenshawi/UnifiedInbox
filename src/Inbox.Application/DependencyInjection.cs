using FluentValidation;
using Inbox.Application.Agents.Validators;
using Inbox.Application.Auth.Validators;
using Microsoft.Extensions.DependencyInjection;

namespace Inbox.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();

        return services;
    }
}