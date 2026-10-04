using FluentValidation;
using Inbox.Application.Common;
using Inbox.Domain.Enums;

namespace Inbox.Application.Channels.Validators;

public sealed class CreateChannelRequestValidator : AbstractValidator<CreateChannelRequest>
{
    public CreateChannelRequestValidator()
    {
        RuleFor(x => x.Type)
            .NotEmpty().WithMessage("Type is required.")
            .Must(type => ChannelTypeParser.TryParse(type, out _))
            .WithMessage("Type must be WhatsApp, Instagram or Facebook.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Display name is required.")
            .MaximumLength(200);

        // This is how a webhook payload is matched to its channel, so it has to be exact.
        RuleFor(x => x.ExternalAccountId)
            .NotEmpty().WithMessage("External account id is required.")
            .MaximumLength(128)
            .Must(id => id.Trim() == id)
            .WithMessage("External account id must not start or end with whitespace.");

        // A connected channel without a token can never send, so refuse it now rather than
        // accepting a channel that only fails later, silently, on the first reply.
        RuleFor(x => x.AccessToken)
            .NotEmpty()
            .When(_ => ChannelTypeParser.TryParse(_.Type, out var type) && type != ChannelType.Test)
            .WithMessage("An access token is required to connect a channel.")
            .MaximumLength(1024);

        RuleFor(x => x.AppSecret).MaximumLength(1024);
        RuleFor(x => x.WebhookVerifyToken).MaximumLength(256);
    }
}

public sealed class UpdateChannelRequestValidator : AbstractValidator<UpdateChannelRequest>
{
    public UpdateChannelRequestValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Display name is required.")
            .MaximumLength(200);

        RuleFor(x => x.ExternalAccountId)
            .MaximumLength(128)
            .Must(id => string.IsNullOrWhiteSpace(id) || id.Trim() == id)
            .WithMessage("External account id must not start or end with whitespace.");

        RuleFor(x => x.AccessToken).MaximumLength(1024);
        RuleFor(x => x.AppSecret).MaximumLength(1024);
        RuleFor(x => x.WebhookVerifyToken).MaximumLength(256);
    }
}