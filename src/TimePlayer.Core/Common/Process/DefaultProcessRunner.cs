using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TimePlayer.Core.Common.Process;

/// <summary>
/// Safe process runner enforcing ProcessStartInfo.ArgumentList, async stream draining,
/// cancellation, timeouts, and process-tree termination.
/// </summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    private readonly ILogger<DefaultProcessRunner> _logger;

    public DefaultProcessRunner(ILogger<DefaultProcessRunner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        IProgress<string>? standardOutputLineProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        options ??= new ProcessRunOptions();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.EnvironmentVariables != null)
        {
            foreach (var (k, v) in options.EnvironmentVariables)
            {
                startInfo.Environment[k] = v;
            }
        }

        // ProcessStartInfo.ArgumentList safely separates arguments without shell escaping
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Starting process {File} with {Count} arguments.", executablePath, arguments.Count);

        var stopwatch = Stopwatch.StartNew();
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var tcsExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A null line marks the end of a stream; output can still be arriving after the process exits.
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcsExit.TrySetResult(process.ExitCode);

        // Asynchronously read stdout
        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult();
                return;
            }
            if (e.Data != null)
            {
                lock (stdoutBuilder)
                {
                    stdoutBuilder.AppendLine(e.Data);
                }
                standardOutputLineProgress?.Report(e.Data);
            }
        };

        // Asynchronously read stderr
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult();
                return;
            }
            if (e.Data != null)
            {
                lock (stderrBuilder)
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {executablePath}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for process exit or timeout/cancellation
            using (timeoutCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogWarning("Killing process tree for {File} due to cancellation/timeout.", executablePath);
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Exception while attempting to kill process {File}.", executablePath);
                }
            }))
            {
                var exitCode = await tcsExit.Task;
                stopwatch.Stop();

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Process '{executablePath}' timed out after {options.Timeout.TotalSeconds} seconds.");
                }

                // Let the last output lines arrive. A child process that inherited the pipes could keep
                // them open, so don't wait forever.
                await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

                string stdout;
                string stderr;
                lock (stdoutBuilder) stdout = stdoutBuilder.ToString();
                lock (stderrBuilder) stderr = stderrBuilder.ToString();

                return new ProcessExecutionResult
                {
                    ExitCode = exitCode,
                    StandardOutput = stdout,
                    StandardError = stderr,
                    Duration = stopwatch.Elapsed
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw new TimeoutException($"Process '{executablePath}' timed out after {options.Timeout.TotalSeconds} seconds.");
        }
    }
}
