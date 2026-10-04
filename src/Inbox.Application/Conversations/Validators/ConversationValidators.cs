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