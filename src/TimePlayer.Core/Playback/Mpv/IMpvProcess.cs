using System.Diagnostics;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Abstraction for the external mpv process lifecycle.
/// Enables deterministic unit testing without spawning native Windows processes.
/// </summary>
public interface IMpvProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    event EventHandler? Exited;
    void Kill();
}

/// <summary>
/// Default production implementation wrapping System.Diagnostics.Process.
/// </summary>
internal sealed class DefaultMpvProcess : IMpvProcess
{
    private readonly Process _process;

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public event EventHandler? Exited;

    public DefaultMpvProcess(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _process.Exited += (s, e) => Exited?.Invoke(this, EventArgs.Empty);

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to launch mpv process: {startInfo.FileName}");
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill errors during shutdown
        }
    }

    public void Dispose()
    {
        _process.Dispose();
    }
}
