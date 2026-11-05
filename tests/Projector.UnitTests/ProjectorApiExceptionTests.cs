using FluentAssertions;
using Projector.Domain.Exceptions;

namespace Projector.UnitTests;

/// <summary>
/// IsSessionInvalid decides whether a tool refreshes the Projector session and runs the call again.
/// Projector answers an expired session with either code; a miss leaves every call failing until
/// the user signs in again.
/// </summary>
public class ProjectorApiExceptionTests
{
    [Theory]
    [InlineData("InvalidSessionTicket", "Session ticket is invalid.")]
    [InlineData("InvalidSession", "The specified session is not valid or is no longer valid. Please re-establish a session.")]
    [InlineData(null, "The specified session is not valid or is no longer valid. Please re-establish a session.")]
    [InlineData(null, "Invalid session.")]
    public void Expired_session_is_detected(string? code, string message) =>
        new ProjectorApiException(message, code).IsSessionInvalid.Should().BeTrue();

    [Theory]
    [InlineData("AccessPermissionDenied", "You do not have permission to view this resource.")]
    [InlineData("ValidationFailed", "The value is not valid for this field.")]
    [InlineData(null, "The task is not valid for time entry.")]
    public void Other_errors_are_not_session_errors(string? code, string message) =>
        new ProjectorApiException(message, code).IsSessionInvalid.Should().BeFalse();
}
