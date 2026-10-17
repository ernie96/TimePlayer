namespace TimePlayer.Core.Common.Process;

/// <summary>
/// Operational options for executing external processes safely.
/// </summary>
public record ProcessRunOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

/// <summary>
/// Result of an external process execution.
/// </summary>
public record ProcessExecutionResult
{
    public int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsSuccess => ExitCode == 0;
}
