namespace TimePlayer.Core.Common.Process;

/// <summary>
/// Abstraction for executing external command-line utilities safely without shell injection.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        IProgress<string>? standardOutputLineProgress = null,
        CancellationToken cancellationToken = default);
}
