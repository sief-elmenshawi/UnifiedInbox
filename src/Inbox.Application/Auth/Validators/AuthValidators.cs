using FluentValidation;

namespace Inbox.Application.Auth.Validators;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Email is not a valid email address.")
            .MaximumLength(256);

        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.");
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().WithMessage("Refresh token is required.");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Current password is required.");

        // The authoritative check is the Identity password validator at runtime; this only rejects
        // requests that could never succeed so the agent gets a 422 instead of a 500.
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("New password is required.")
            .MinimumLength(12).WithMessage("New password must be at least 12 characters long.")
            .MaximumLength(128).WithMessage("New password must be at most 128 characters long.");
    }
}