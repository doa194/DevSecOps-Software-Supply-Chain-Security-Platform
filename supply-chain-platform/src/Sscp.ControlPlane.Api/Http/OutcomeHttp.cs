// Turns domain outcomes into HTTP responses. Rule violations become RFC 9457 problem
// responses carrying a stable `code`, so pipelines can react to specific failures.
using Sscp.ControlPlane.Domain.Common;

namespace Sscp.ControlPlane.Api.Http;

public static class OutcomeHttp
{
    public static IResult Problem(DomainError error) => Results.Problem(
        title: error.Message,
        statusCode: error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status409Conflict,
        },
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    public static IResult ToHttp(this Outcome outcome) =>
        outcome.Succeeded ? Results.NoContent() : Problem(outcome.Error!);

    public static IResult ToHttp<T>(this Outcome<T> outcome, Func<T, object> body) =>
        outcome.Succeeded ? Results.Ok(body(outcome.Value)) : Problem(outcome.Error!);
}
