using TimePlayer.Core.Playback.Mpv;

namespace TimePlayer.Tests.Helpers;

public class FakeMpvProcess : IMpvProcess
{
    public bool HasExited { get; set; }
    public int ExitCode { get; set; }
    public event EventHandler? Exited;
    public bool WasKilled { get; private set; }
    public bool WasDisposed { get; private set; }

    public void Kill()
    {
        WasKilled = true;
        HasExited = true;
    }

    public void TriggerExit(int exitCode)
    {
        ExitCode = exitCode;
        HasExited = true;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        WasDisposed = true;
    }
}
