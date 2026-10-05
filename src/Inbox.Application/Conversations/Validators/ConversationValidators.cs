using FluentValidation;
using Inbox.Application.Conversations;

namespace Inbox.Application.Conversations.Validators;

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage("Message text is required.")
            .MaximumLength(4096).WithMessage("Message text must be at most 4096 characters.");
    }
}

public sealed class TransferConversationRequestValidator : AbstractValidator<TransferConversationRequest>
{
    public TransferConversationRequestValidator()
    {
        RuleFor(x => x.TargetAgentId)
            .NotEmpty().WithMessage("Target agent is required.");
    }
}

public sealed class SendTemplateRequestValidator : AbstractValidator<SendTemplateRequest>
{
    public SendTemplateRequestValidator()
    {
        // Meta only accepts letters, digits and underscores in a template name, and the language code
        // is a locale such as "en" or "ar". Both are checked here rather than being forwarded to
        // produce a provider error on an already-stored message.
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Template name is required.")
            .MaximumLength(512)
            .Matches("^[A-Za-z0-9_]+$").WithMessage("Template name may only contain letters, digits and underscores.");

        RuleFor(x => x.LanguageCode)
            .NotEmpty().WithMessage("Template language code is required.")
            .MaximumLength(16)
            .Matches("^[A-Za-z]{2}(_[A-Za-z]{2,4})?$").WithMessage("Language code must look like 'en' or 'en_US'.");

        RuleFor(x => x.BodyParameters)
            .NotNull().WithMessage("Body parameters are required.")
            .Must(x => x.Count <= 20).WithMessage("A template can take at most 20 body parameters.")
            .Must(x => x.All(p => p is not null && p.Length <= 1024))
            .WithMessage("Each body parameter must be at most 1024 characters.");

        RuleFor(x => x.Category)
            .MaximumLength(512).WithMessage("Category must be at most 512 characters.")
            .Matches("^[A-Za-z0-9_]*$").WithMessage("Category may only contain letters, digits and underscores.");
    }
}