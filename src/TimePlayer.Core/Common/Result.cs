using System.Diagnostics.CodeAnalysis;

namespace TimePlayer.Core.Common;

/// <summary>
/// Strongly-typed operation result that encapsulates success with a value or failure with an AppError.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly AppError? _error;

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    [MemberNotNullWhen(false, nameof(Value))]
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public T? Value => IsSuccess ? _value : default;
    public AppError? Error => IsFailure ? _error : null;

    private Result(T value)
    {
        IsSuccess = true;
        _value = value;
        _error = null;
    }

    private Result(AppError error)
    {
        IsSuccess = false;
        _value = default;
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(AppError error) => new(error);
    public static Result<T> Failure(string code, string message, string? details = null) =>
        new(new AppError { Code = code, Message = message, TechnicalDetails = details });

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(AppError error) => Failure(error);
}

/// <summary>
/// Non-generic operation result for void/empty operations.
/// </summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public AppError? Error { get; }

    private Result(bool isSuccess, AppError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(AppError error) => new(false, error ?? throw new ArgumentNullException(nameof(error)));
    public static Result Failure(string code, string message, string? details = null) =>
        new(false, new AppError { Code = code, Message = message, TechnicalDetails = details });

    public static implicit operator Result(AppError error) => Failure(error);
}
