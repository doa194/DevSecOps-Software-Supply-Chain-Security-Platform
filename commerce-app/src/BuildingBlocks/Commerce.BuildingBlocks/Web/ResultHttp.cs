// Turns Result values into HTTP responses in one place, so every endpoint reports errors
// the same way (RFC 9457 problem details) and never leaks internal exception text.
using Commerce.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Commerce.BuildingBlocks.Web;

public static class ResultHttp
{
    public static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return TypedResults.Problem(title: error.Code, detail: error.Message, statusCode: status);
    }

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    public static IResult ToHttp(this Result result, Func<IResult> onSuccess) =>
        result.IsSuccess ? onSuccess() : result.Error.ToProblem();
}
