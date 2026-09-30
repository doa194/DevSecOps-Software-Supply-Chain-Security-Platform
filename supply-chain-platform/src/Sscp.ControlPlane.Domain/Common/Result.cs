// Result type for the Control Plane domain: expected rule violations are values, not
// exceptions, so callers must decide what to do with them.
namespace Sscp.ControlPlane.Domain.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unavailable,
}

public sealed record DomainError(string Code, string Message, ErrorKind Kind = ErrorKind.Conflict)
{
    public static DomainError Validation(string code, string message) => new(code, message, ErrorKind.Validation);
    public static DomainError NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static DomainError Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static DomainError Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);
    public static DomainError Unavailable(string code, string message) => new(code, message, ErrorKind.Unavailable);
}

public class Outcome
{
    protected Outcome(DomainError? error) => Error = error;

    public DomainError? Error { get; }
    public bool Succeeded => Error is null;

    public static Outcome Ok() => new(null);
    public static Outcome Fail(DomainError error) => new(error);
    public static Outcome<T> Ok<T>(T value) => new(value, null);

    public static implicit operator Outcome(DomainError error) => Fail(error);
}

public sealed class Outcome<T> : Outcome
{
    private readonly T? _value;

    internal Outcome(T? value, DomainError? error) : base(error) => _value = value;

    public T Value => Succeeded ? _value! : throw new InvalidOperationException($"No value: {Error!.Code}");

    public static implicit operator Outcome<T>(T value) => new(value, null);
    public static implicit operator Outcome<T>(DomainError error) => new(default, error);
}
