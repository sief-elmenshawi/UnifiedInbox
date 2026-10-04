using FluentValidation;
using Inbox.Application.Common;
using Inbox.Domain.Enums;

namespace Inbox.Application.Agents.Validators;

public sealed class CreateAgentRequestValidator : AbstractValidator<CreateAgentRequest>
{
    public CreateAgentRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Email is not a valid email address.")
            .MaximumLength(256);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Display name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(role => AgentRoleParser.TryParse(role, out _))
            .WithMessage($"Role must be '{AgentRoleParser.Admin}' or '{AgentRoleParser.Agent}'.");
    }
}

public sealed class UpdateAgentRequestValidator : AbstractValidator<UpdateAgentRequest>
{
    public UpdateAgentRequestValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Display name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .Must(role => AgentRoleParser.TryParse(role, out _))
            .WithMessage($"Role must be '{AgentRoleParser.Admin}' or '{AgentRoleParser.Agent}'.");
    }
}

public sealed class SetPresenceRequestValidator : AbstractValidator<SetPresenceRequest>
{
    public SetPresenceRequestValidator()
    {
        RuleFor(x => x.PresenceStatus)
            .NotEmpty().WithMessage("Presence status is required.")
            .Must(status => Enum.TryParse<PresenceStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            .WithMessage("Presence status must be Online, Away or Offline.");
    }
}