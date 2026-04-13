namespace NL2Mongo.Api.Helpers;

public enum ErrorType
{
    NotFound,
    Validation,
    Conflict,
    Internal
}

public record Error(string Message, ErrorType Type = ErrorType.Internal);

public class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    private Result(T value) { IsSuccess = true; Value = value; }
    private Result(Error error) { IsSuccess = false; Error = error; }

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);
}

public static class ResultExtensions
{
    public static IResult ToApiResult<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error!.Type switch
        {
            ErrorType.NotFound   => Results.NotFound(new { error = result.Error.Message }),
            ErrorType.Validation => Results.BadRequest(new { error = result.Error.Message }),
            ErrorType.Conflict   => Results.Conflict(new { error = result.Error.Message }),
            _                    => Results.Problem(result.Error.Message)
        };
    }
}
