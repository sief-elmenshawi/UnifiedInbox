using Inbox.Domain.Common;
using FluentAssertions;

namespace Inbox.Domain.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccess_And_NoError()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_CarriesTheError()
    {
        var error = Error.Conflict("conversation.already_taken", "Already taken by Saif.");

        var result = Result.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Success_WithValue_ExposesValue()
    {
        var result = Result.Success("value");

        result.Value.Should().Be("value");
    }

    [Fact]
    public void Value_OnFailedResult_Throws()
    {
        var result = Result.Failure<string>(Error.NotFound("x", "missing"));

        var act = () => result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SuccessfulResult_CannotCarryAnError()
    {
        var act = () => Result.Failure<object>(Error.NotFound("x", "missing")).IsSuccess.Should().BeFalse();

        act.Should().NotThrow();
    }

    [Fact]
    public void ImplicitConversions_Work()
    {
        Result<int> fromValue = 42;
        Result<int> fromError = Error.Validation("bad", "invalid");

        fromValue.IsSuccess.Should().BeTrue();
        fromValue.Value.Should().Be(42);
        fromError.IsFailure.Should().BeTrue();
        fromError.Error!.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void ErrorFactories_SetTheType()
    {
        Error.Validation("a", "a").Type.Should().Be(ErrorType.Validation);
        Error.NotFound("b", "b").Type.Should().Be(ErrorType.NotFound);
        Error.Conflict("c", "c").Type.Should().Be(ErrorType.Conflict);
        Error.Unauthorized("d", "d").Type.Should().Be(ErrorType.Unauthorized);
        Error.Forbidden("e", "e").Type.Should().Be(ErrorType.Forbidden);
        Error.Failure("f", "f").Type.Should().Be(ErrorType.Failure);
    }
}