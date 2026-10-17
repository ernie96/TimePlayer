using TimePlayer.Core.Common.Process;

namespace TimePlayer.Tests.Helpers;

/// <summary>
/// Fake implementation of IProcessRunner for deterministic unit testing without spawning real binaries.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public record Invocation(string Executable, IReadOnlyList<string> Arguments, ProcessRunOptions? Options);

    private readonly List<Invocation> _invocations = new();
    public IReadOnlyList<Invocation> Invocations => _invocations;

    public ProcessExecutionResult NextResult { get; set; } = new()
    {
        ExitCode = 0,
        StandardOutput = string.Empty,
        StandardError = string.Empty,
        Duration = TimeSpan.FromMilliseconds(10)
    };

    public Exception? NextException { get; set; }

    public Func<string, IReadOnlyList<string>, IProgress<string>?, ProcessExecutionResult>? Handler { get; set; }

    public Task<ProcessExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        IProgress<string>? standardOutputLineProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _invocations.Add(new Invocation(executablePath, arguments, options));

        if (NextException != null)
        {
            throw NextException;
        }

        if (Handler != null)
        {
            return Task.FromResult(Handler(executablePath, arguments, standardOutputLineProgress));
        }

        return Task.FromResult(NextResult);
    }
}
